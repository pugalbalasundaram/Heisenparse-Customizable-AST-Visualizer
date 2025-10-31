using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Msagl.Drawing;
using Microsoft.Msagl.GraphViewerGdi;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.VisualBasic;
using HeisenParserWPF.Services;
using Supabase.Gotrue;

namespace HeisenParserWPF.Pages
{
    #region AST Model and Tree Builder (Modified for Sequential Flow & Scoping)

    public class AstNode
    {
        public string Label { get; set; }
        public string Type { get; set; }
        public string Kind { get; set; }
        public List<AstNode> Children { get; set; }
        public List<AstNode> SequentialSiblings { get; set; } = new List<AstNode>();

        // NEW: For Variable Lifetime Visualization
        public List<string> AccessibleVariables { get; set; } = new List<string>();

        public AstNode(string label, string type = "Node", string kind = "")
        {
            Label = label ?? string.Empty;
            Type = type;
            Kind = kind;
            Children = new List<AstNode>();
        }
        public void AddChild(AstNode child)
        {
            if (child != null) Children.Add(child);
        }
        public void AddSequentialSibling(AstNode sibling)
        {
            if (sibling != null) SequentialSiblings.Add(sibling);
        }
    }

    public interface ITreeBuilder
    {
        AstNode BuildTree(string code);
    }

    public class RoslynTreeBuilder : ITreeBuilder
    {
        public AstNode BuildTree(string code)
        {
            var tree = CSharpSyntaxTree.ParseText(code);
            var root = tree.GetRoot();
            return BuildNodeRecursive(root);
        }

        private AstNode BuildNodeRecursive(SyntaxNode node, List<string>? currentScopeVariables = null)
        {
            currentScopeVariables ??= new List<string>();
            AstNode n;

            // --- Structural Nodes ---
            if (node is CompilationUnitSyntax cu)
            {
                n = new AstNode("Root", node.GetType().Name, node.Kind().ToString());
                foreach (var member in cu.Members)
                    n.AddChild(BuildNodeRecursive(member, currentScopeVariables));
                return n;
            }
            if (node is NamespaceDeclarationSyntax ns)
            {
                n = new AstNode($"namespace {ns.Name}", node.GetType().Name, node.Kind().ToString());
                foreach (var member in ns.Members)
                    n.AddChild(BuildNodeRecursive(member, currentScopeVariables));
                return n;
            }
            if (node is ClassDeclarationSyntax cls)
            {
                n = new AstNode($"class {cls.Identifier.Text}", node.GetType().Name, node.Kind().ToString());
                foreach (var member in cls.Members)
                    n.AddChild(BuildNodeRecursive(member, currentScopeVariables));
                return n;
            }
            if (node is MethodDeclarationSyntax m)
            {
                n = new AstNode($"{m.Identifier.Text}()", node.GetType().Name, node.Kind().ToString());

                // Method scope starts here: new variable list
                var methodScopeVars = new List<string>(currentScopeVariables);

                foreach (var p in m.ParameterList.Parameters)
                {
                    methodScopeVars.Add(p.Identifier.Text);
                    n.AddChild(new AstNode(p.Identifier.Text, p.GetType().Name, p.Kind().ToString()));
                }

                if (m.Body != null)
                {
                    BuildSequentialStatements(n, m.Body.Statements, methodScopeVars);
                }
                n.AccessibleVariables = new List<string>(methodScopeVars);
                return n;
            }

            // --- Flow Control Nodes ---
            if (node is BlockSyntax block)
            {
                n = new AstNode("{ ... }", node.GetType().Name, node.Kind().ToString());

                // New block scope starts here: copy variables from parent
                var blockScopeVars = new List<string>(currentScopeVariables);

                BuildSequentialStatements(n, block.Statements, blockScopeVars);
                n.AccessibleVariables = new List<string>(blockScopeVars);
                return n;
            }
            if (node is IfStatementSyntax ifs)
            {
                n = new AstNode($"if ({Shorten(ifs.Condition?.ToString() ?? "")})", node.GetType().Name, node.Kind().ToString());

                // If statement branches
                if (ifs.Statement != null) n.AddChild(BuildNodeRecursive(ifs.Statement, currentScopeVariables));
                if (ifs.Else != null && ifs.Else.Statement != null) n.AddChild(BuildNodeRecursive(ifs.Else.Statement, currentScopeVariables));

                n.AccessibleVariables = new List<string>(currentScopeVariables);
                return n;
            }

            // --- Statement Nodes ---
            if (node is LocalDeclarationStatementSyntax localDecl)
            {
                var decl = localDecl.Declaration;
                var v = decl.Variables.FirstOrDefault();
                if (v != null)
                {
                    // Add declared variable to current scope
                    currentScopeVariables.Add(v.Identifier.Text);

                    n = new AstNode($"{v.Identifier.Text} = {(v.Initializer != null ? v.Initializer.Value.ToString() : "<no init>")}", decl.GetType().Name, decl.Kind().ToString());
                    n.AddChild(new AstNode(v.Identifier.Text, "Identifier", v.Kind().ToString()));
                    if (v.Initializer != null)
                        n.AddChild(BuildExpressionNode(v.Initializer.Value));

                    n.AccessibleVariables = new List<string>(currentScopeVariables);
                    return n;
                }
            }
            if (node is ExpressionStatementSyntax exprStmt)
            {
                n = BuildExpressionNode(exprStmt.Expression);
                n.AccessibleVariables = new List<string>(currentScopeVariables);
                return n;
            }

            // --- Default Fallback ---
            string codeStr = node.ToString().Trim();
            if (codeStr.Length > 60) codeStr = codeStr.Substring(0, 57) + "...";
            n = new AstNode(codeStr.Replace("\r\n", " ").Replace("\n", " "), node.GetType().Name, node.Kind().ToString());
            n.AccessibleVariables = new List<string>(currentScopeVariables);
            return n;
        }

        // MODIFIED: Takes currentScopeVariables to track local variables
        private void BuildSequentialStatements(AstNode parentNode, System.Collections.Generic.IReadOnlyList<StatementSyntax> statements, List<string> currentScopeVariables)
        {
            AstNode? previousNode = null;

            foreach (var stmt in statements)
            {
                // Pass a COPY of the variables to handle declarations within the statement
                var currentNode = BuildNodeRecursive(stmt, currentScopeVariables);

                parentNode.AddChild(currentNode);

                if (previousNode != null)
                {
                    previousNode.AddSequentialSibling(currentNode);
                }
                previousNode = currentNode;
            }
        }


        private AstNode BuildExpressionNode(ExpressionSyntax expr)
        {
            // Simplified BuildExpressionNode (doesn't track scope changes but is fine for expressions)
            if (expr == null) return new AstNode("<expr?>", "Expression", "");
            AstNode n;
            switch (expr)
            {
                case LiteralExpressionSyntax lit:
                    n = new AstNode(lit.Token.Text, lit.GetType().Name, lit.Kind().ToString()); break;
                case IdentifierNameSyntax id:
                    n = new AstNode(id.Identifier.Text, id.GetType().Name, id.Kind().ToString()); break;
                case BinaryExpressionSyntax bin:
                    n = new AstNode(Shorten(bin.OperatorToken.Text), bin.GetType().Name, bin.Kind().ToString());
                    n.AddChild(BuildExpressionNode(bin.Left));
                    n.AddChild(BuildExpressionNode(bin.Right));
                    break;
                case InvocationExpressionSyntax inv:
                    n = new AstNode($"{inv.Expression}()", inv.GetType().Name, inv.Kind().ToString());
                    if (inv.ArgumentList != null)
                        foreach (var arg in inv.ArgumentList.Arguments)
                            n.AddChild(BuildExpressionNode(arg.Expression));
                    break;
                case AssignmentExpressionSyntax assign:
                    n = new AstNode(assign.OperatorToken.Text, assign.GetType().Name, assign.Kind().ToString());
                    n.AddChild(BuildExpressionNode(assign.Left));
                    n.AddChild(BuildExpressionNode(assign.Right));
                    break;
                default:
                    string code = expr.ToString().Trim();
                    if (code.Length > 60) code = code.Substring(0, 57) + "...";
                    n = new AstNode(code.Replace("\r\n", " ").Replace("\n", " "), expr.GetType().Name, expr.Kind().ToString());
                    break;
            }
            return n;
        }
        private string Shorten(string s, int max = 30) => s.Length > max ? s.Substring(0, max) + "…" : s;
    }

    #endregion

    public partial class EditExistingCodePage : Page
    {
        private readonly Frame _mainFrame;
        private string _filePath;
        private readonly GViewer _viewer;
        private readonly DispatcherTimer _updateTimer;
        private AstNode? rootAst;
        private readonly ITreeBuilder treeBuilder = new RoslynTreeBuilder();
        private readonly Dictionary<string, AstNode> graphIdToAst = new Dictionary<string, AstNode>();
        private static readonly HttpClient httpClient = new HttpClient();
        private Process? ollamaProcess = null;
        private bool startedBundledOllama = false;
        private readonly int ollamaPort = 11434;
        private readonly User? _currentUser;

        private string? _selectedNodeId = null;
        private TabControl? _tabControl;

        public EditExistingCodePage(Frame mainFrame, string filePath)
        {
            InitializeComponent();
            _mainFrame = mainFrame;
            _filePath = filePath;

            // FIX: Get the reference to the TabControl after InitializeComponent() runs
            _tabControl = (TabControl)this.FindName("MainTabControl");


            // ADDED: Get the current user from the central client manager
            _currentUser = SupabaseClientManager.Client?.Auth?.CurrentUser;

            _viewer = new GViewer
            {
                Dock = System.Windows.Forms.DockStyle.Fill,
                ToolBarIsVisible = false,
                // Enable dragging of graph (panning/moving nodes)
                LayoutEditingEnabled = true,
                AllowDrop = true
            };

            // MSAGL automatically supports panning by middle-click and dragging nodes by left-click when LayoutEditingEnabled=true
            _viewer.MouseDoubleClick += Viewer_MouseDoubleClick;
            _viewer.DragEnter += Viewer_DragEnter;
            _viewer.DragDrop += Viewer_DragDrop;
            // NEW: Added Click handler for AI context activation
            _viewer.MouseClick += Viewer_MouseClick;
            GraphHost.Child = _viewer;

            _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _updateTimer.Tick += (s, e) => { _updateTimer.Stop(); UpdateAstAndJson(); };

            // NOTE: Code-to-Node synchronization (CodeEditor.SelectionChanged) requires complex line mapping 
            // from Roslyn, which is beyond simple property access and omitted here for a working solution.

            LoadFile(_filePath);
            _ = StartBundledOllamaIfNeededAsync();
        }

        private void LoadFile(string filePath)
        {
            if (File.Exists(filePath))
            {
                _filePath = filePath;
                FilePathTextBlock.Text = $"Editing file: {Path.GetFileName(_filePath)}";
                CodeEditor.Text = File.ReadAllText(_filePath);
                UpdateAstAndJson();
            }
        }

        #region Core Update and Visualization Logic

        private void UpdateAstAndJson()
        {
            try
            {
                rootAst = treeBuilder.BuildTree(CodeEditor.Text);
                PersistAstJson();
                BuildAstGraphFromNode(rootAst);
            }
            catch { /* Suppress parsing errors during typing */ }
        }

        private void PersistAstJson()
        {
            try
            {
                if (rootAst != null)
                {
                    File.WriteAllText("ast.json", JsonSerializer.Serialize(rootAst, new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            catch { /* Suppress IO errors */ }
        }

        private void BuildAstGraphFromNode(AstNode root)
        {
            graphIdToAst.Clear();
            // Configure MSAGL for a flow-chart like layout (bus topology)
            Graph g = new Graph("AST")
            {
                Attr =
                {
                    LayerDirection = LayerDirection.TB, // Top to Bottom
                    NodeSeparation = 10,                 // Small node separation for 'bus' look
                    MinNodeWidth = 50,                   // Ensure nodes are visible
                    MinNodeHeight = 30
                }
            };

            AddNodeToGraph(g, root, null);

            // NEW: Iterate through all nodes to draw sequential flow edges (the "Bus")
            foreach (var node in graphIdToAst.Values)
            {
                string? currentId = graphIdToAst.FirstOrDefault(x => x.Value == node).Key;

                foreach (var sibling in node.SequentialSiblings)
                {
                    string? siblingId = graphIdToAst.FirstOrDefault(x => x.Value == sibling).Key;
                    if (!string.IsNullOrEmpty(currentId) && !string.IsNullOrEmpty(siblingId))
                    {
                        var edge = g.AddEdge(currentId, siblingId);
                        // Using a simple arrow for sequential flow
                        edge.Attr.ArrowheadAtTarget = ArrowStyle.Normal;
                        edge.Attr.Color = Microsoft.Msagl.Drawing.Color.DimGray;
                        edge.Attr.LineWidth = 2;
                    }
                }
            }

            // NOTE: "Micro-Animation for Structural Changes" requires a rendering loop with interpolation 
            // which is outside the scope of basic GViewer calls.

            _viewer.Graph = g;
        }

        private void AddNodeToGraph(Graph g, AstNode node, string? parentId)
        {
            string nodeId = Guid.NewGuid().ToString();
            var gNode = g.AddNode(nodeId);
            gNode.LabelText = node.Label;
            gNode.Id = nodeId;

            // Bus Topology / Control Flow Styling
            string kind = node.Kind ?? "";

            // NEW: Complexity Heatmap (Placeholder logic based on nesting/kind)
            int complexity = node.Children.Count(c => c.Kind.Contains("If") || c.Kind.Contains("For"));
            Microsoft.Msagl.Drawing.Color fillColor = Microsoft.Msagl.Drawing.Color.LightGray;

            if (kind.Contains("Method") || kind.Contains("Block"))
            {
                if (complexity > 3) fillColor = Microsoft.Msagl.Drawing.Color.Red;
                else if (complexity > 1) fillColor = Microsoft.Msagl.Drawing.Color.Orange;
                else fillColor = Microsoft.Msagl.Drawing.Color.LightGreen;
            }
            else if (kind.Contains("If") || kind.Contains("For") || kind.Contains("While"))
            {
                fillColor = Microsoft.Msagl.Drawing.Color.Orange;
            }
            else if (kind.Contains("Class") || kind.Contains("Namespace") || kind.Contains("CompilationUnit"))
            {
                // Structural Context Panel: These nodes should ideally be filtered, 
                // but we keep coloring for clarity in the current full AST view.
                fillColor = Microsoft.Msagl.Drawing.Color.LightBlue;
            }

            gNode.Attr.Shape = kind.Contains("If") || kind.Contains("For") || kind.Contains("While") ? Shape.Diamond : Shape.Box;
            gNode.Attr.FillColor = fillColor;
            gNode.Label.FontColor = kind.Contains("Block") ? Microsoft.Msagl.Drawing.Color.White : Microsoft.Msagl.Drawing.Color.Black;

            graphIdToAst[nodeId] = node;

            if (!string.IsNullOrEmpty(parentId))
            {
                if (graphIdToAst.TryGetValue(parentId, out var parentNode))
                {
                    if (!parentNode.Kind.Contains("Method") && !parentNode.Kind.Contains("Block"))
                    {
                        var edge = g.AddEdge(parentId, nodeId);
                        edge.Attr.ArrowheadAtTarget = ArrowStyle.Normal;
                        edge.Attr.Color = Microsoft.Msagl.Drawing.Color.Gray;
                    }
                }
            }

            foreach (var child in node.Children)
            {
                AddNodeToGraph(g, child, nodeId);
            }
        }

        #endregion

        #region Tree Editing Handlers (Node Edit/Drop, AI Interaction)

        private void Viewer_MouseDoubleClick(object? sender, System.Windows.Forms.MouseEventArgs e)
        {
            try
            {
                var obj = _viewer.ObjectUnderMouseCursor;
                if (obj?.DrawingObject is Microsoft.Msagl.Drawing.Node gNode)
                {
                    _selectedNodeId = gNode.Id;
                    string currentLabel = gNode.LabelText ?? "";

                    // NEW: Predictive Code Completion in Graph / Structural Snippet Expansion (Step 2)
                    string message = "Edit node label (Use natural language or C#). The AI will attempt to convert natural language to C# syntax:";

                    if (currentLabel.Contains("// TODO"))
                    {
                        message = "This is a placeholder. Enter the statement you want to generate (e.g., 'loop 5 times', 'set total to price plus tax'):";
                    }

                    string newLabel = Interaction.InputBox(message, "Edit Node", currentLabel);

                    if (!string.IsNullOrEmpty(newLabel) && newLabel != currentLabel)
                    {
                        // Simulate natural language conversion via AI 
                        Task.Run(async () =>
                        {
                            // NEW: Using the AI to clean and complete the label
                            string aiPrompt = $"Convert the user text '{newLabel}' into a brief, syntactically correct C# expression or statement label. Only return the C# code, nothing else. If it is already code or an empty placeholder, return it as is. Example: 'if age is greater than 18' becomes 'if (age > 18)'.";
                            string suggestedCode = await SendToOllama(aiPrompt);

                            Dispatcher.Invoke(() =>
                            {
                                gNode.LabelText = suggestedCode;
                                if (graphIdToAst.TryGetValue(gNode.Id, out var astNode))
                                {
                                    astNode.Label = suggestedCode;
                                    PersistAstJson();
                                }
                                _viewer.Invalidate();
                            });
                        });
                    }
                }
            }
            catch { /* Suppress errors */ }
        }

        // NEW METHOD: Handle single click to activate AI Context
        private async void Viewer_MouseClick(object? sender, System.Windows.Forms.MouseEventArgs e)
        {
            var obj = _viewer.ObjectUnderMouseCursor;
            if (obj?.DrawingObject is Microsoft.Msagl.Drawing.Node gNode)
            {
                _selectedNodeId = gNode.Id;
                if (graphIdToAst.TryGetValue(gNode.Id, out var astNode))
                {
                    // 1. Context Summary: Update AI Assistant box with context
                    if (_tabControl != null && _tabControl.Items.Cast<TabItem>().LastOrDefault() is TabItem aiTab)
                    {
                        aiTab.IsSelected = true;
                    }

                    string varList = astNode.AccessibleVariables.Any() ? string.Join(", ", astNode.AccessibleVariables) : "None";

                    AiResponseBox.Text = $"--- Context Summary ---\n" +
                                         $"Selected Node: '{astNode.Label}'\n" +
                                         $"Kind: {astNode.Kind}\n" +
                                         $"Variables in Scope: {varList}\n" + // Variable Lifetime Visualization
                                         $"-----------------------\n";

                    // 2. Contextual AI Suggestions: Generate suggestions based on node kind
                    await GenerateSuggestionsAsync(astNode);

                    // NOTE: Node Pulse Feedback (Micro-Animation) would be triggered here: 
                    // gNode.Attr.LineWidth = 3; gNode.Attr.Color = Microsoft.Msagl.Drawing.Color.Yellow; _viewer.Invalidate(); 
                    // and then a delayed reset to the original style.
                }
            }
        }

        private async Task GenerateSuggestionsAsync(AstNode contextNode)
        {
            string prompt = "";
            string currentAstJson = JsonSerializer.Serialize(contextNode, new JsonSerializerOptions { WriteIndented = true });

            // Check if the current node is a Method/Block and if it's the last in its flow (simple heuristic)
            bool isFlowEnd = !contextNode.SequentialSiblings.Any();

            if ((contextNode.Kind.Contains("Method") || contextNode.Kind.Contains("Block")) && isFlowEnd)
            {
                prompt = $"The user is editing a code block and is at the end of the current flow. Available variables: {string.Join(", ", contextNode.AccessibleVariables)}. Suggest the top 5 next *statements* as clickable commands in markdown, e.g., '[Add assignment: x = 0]', '[Add loop: for]' . Focus on flow control or simple actions.";
            }
            else if (contextNode.Kind.Contains("IfStatement"))
            {
                prompt = $"The user selected this conditional node: {currentAstJson}. Suggest the top 3 logical follow-up actions for the branches as clickable markdown commands: one for the 'true' branch, one for the 'false' branch, and one for a common statement. E.g., '[Add 'true' statement: Log success]', '[Add 'else' block]', '[Add return statement]'.";
            }
            else if (contextNode.Kind.Contains("Declaration") || contextNode.Kind.Contains("Assignment"))
            {
                prompt = $"The user selected an action node: {contextNode.Label}. Available variables: {string.Join(", ", contextNode.AccessibleVariables)}. Suggest the top 3 immediate usage ideas for the variables in scope as clickable markdown commands. E.g., '[Use in calculation: variable = value * 2]', '[Print variable: Console.WriteLine(variable)]', '[Add null check: if (variable != null)]'.";
            }

            if (!string.IsNullOrEmpty(prompt))
            {
                AiResponseBox.AppendText("\n> HeisenParse AI is thinking...\n");

                try
                {
                    string aiResponse = await SendToOllama(prompt);
                    AiResponseBox.AppendText($"\n> **Suggestions:**\n{aiResponse}\n");
                }
                catch (Exception ex)
                {
                    AiResponseBox.AppendText($"\n> [Error] Could not fetch suggestions: {ex.Message}\n");
                }
            }
        }

        private void Viewer_DragEnter(object? sender, System.Windows.Forms.DragEventArgs e)
        {
            // NOTE: For the "Drag-Preview Ghost Node" feature, this is where you would initiate 
            // the custom drawing of the ghost node and check for valid insertion points.
            if (e.Data.GetDataPresent(DataFormats.StringFormat))
                e.Effect = System.Windows.Forms.DragDropEffects.Copy;
            else
                e.Effect = System.Windows.Forms.DragDropEffects.None;
        }

        private void Viewer_DragDrop(object? sender, System.Windows.Forms.DragEventArgs e)
        {
            // NOTE: Context-Aware Insertion (between nodes) and Structural Re-Parenting logic 
            // is complex. This is the existing logic for sequential APPENDING to the closest valid block.
            try
            {
                if (!e.Data.GetDataPresent(DataFormats.StringFormat)) return;
                string payload = (string)e.Data.GetData(DataFormats.StringFormat) ?? "";
                string type = payload.StartsWith("type:") ? payload.Substring(5) : payload;

                AstNode? parentAst = null;

                var targetGNode = _viewer.ObjectUnderMouseCursor?.DrawingObject as Microsoft.Msagl.Drawing.Node;
                if (targetGNode != null && !string.IsNullOrEmpty(targetGNode.Id) && graphIdToAst.TryGetValue(targetGNode.Id, out var mapped))
                {
                    if (mapped.Kind.Contains("Method") || mapped.Kind.Contains("Block"))
                    {
                        parentAst = mapped;
                    }
                    else
                    {
                        parentAst = FindClosestLogicalParent(rootAst, mapped);
                    }
                }

                if (parentAst == null && rootAst != null)
                {
                    parentAst = FindClosestLogicalParent(rootAst, type);
                }

                if (parentAst == null)
                {
                    if (rootAst == null) rootAst = new AstNode("Root", "Root", "CompilationUnit");
                    parentAst = rootAst;
                }

                AstNode newNode = CreateAstNodeForType(type);

                if (parentAst.Kind.Contains("Method") || parentAst.Kind.Contains("Block"))
                {
                    parentAst.AddChild(newNode);

                    AstNode? lastStatement = parentAst.Children
                        .LastOrDefault(c => !c.Kind.Contains("Block") && !c.Kind.Contains("Parameter"));

                    if (lastStatement != null && lastStatement != newNode)
                    {
                        lastStatement.SequentialSiblings.Clear();
                        lastStatement.AddSequentialSibling(newNode);
                    }
                }
                else
                {
                    parentAst.AddChild(newNode);
                }

                PersistAstJson();
                BuildAstGraphFromNode(rootAst);
                // NOTE: Node Pulse Feedback (Micro-Animation) would be triggered here on the inserted node.
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error while dropping node: " + ex.Message);
            }
        }

        // Helper to find the container block/method of a given node (Overload 1)
        private AstNode? FindClosestLogicalParent(AstNode? current, AstNode target)
        {
            if (current == null) return null;

            foreach (var child in current.Children)
            {
                if (child == target)
                {
                    return current;
                }

                if (current.Kind.Contains("Method") || current.Kind.Contains("Block"))
                {
                    var foundInChild = FindClosestLogicalParent(child, target);
                    if (foundInChild != null)
                    {
                        return current;
                    }
                }
            }
            return null;
        }

        // Overload for initial call when drag type is unknown (fallback) (Overload 2)
        private AstNode? FindClosestLogicalParent(AstNode currentRoot, string newNodeType)
        {
            AstNode? bestParent = null;
            void Search(AstNode node)
            {
                if (node.Kind.Contains("Method") || node.Kind.Contains("Block"))
                {
                    bestParent = node;
                }
                foreach (var child in node.Children) Search(child);
            }
            Search(currentRoot);
            return bestParent;
        }

        // NEW METHOD: Handle AI Fix button click (Part of Refactoring on the Fly)
        private async void AI_Fix_Click(object sender, RoutedEventArgs e)
        {
            AiResponseBox.AppendText("\n> **AI Refactoring:** Attempting to fix code structure...\n");

            // NOTE: In a real implementation, you would first get Roslyn's error diagnostics 
            // from the last parse attempt, but here we just send the code.

            string prompt = $"The following C# code has potential syntax errors or structural issues. Analyze the code and rewrite a syntactically correct, cleaner version of the C# code. Only output the corrected C# code, nothing else, so I can replace the content of my code editor. Code: \n\n{CodeEditor.Text}";

            try
            {
                string correctedCode = await SendToOllama(prompt);

                if (!string.IsNullOrWhiteSpace(correctedCode))
                {
                    // Update the code editor with the AI-suggested fix
                    CodeEditor.Text = correctedCode.Trim();
                    UpdateAstAndJson(); // Re-parse and redraw immediately
                    AiResponseBox.AppendText("\n> **AI FIX SUCCESS:** Code corrected and graph redrawn. Please review.\n");
                }
                else
                {
                    AiResponseBox.AppendText("\n> **AI FIX FAILED:** AI returned no code or an empty response.\n");
                }
            }
            catch (Exception ex)
            {
                AiResponseBox.AppendText($"\n> [Error] AI Refactoring failed: {ex.Message}\n");
            }
        }

        #endregion

        #region UI Event Handlers (Buttons, Zoom)

        private void Snippet_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // NEW: Added dynamic suggestion for initial label of the dragged node (Step 2 Implementation)
            if (sender is Button button && button.Tag is string payload)
            {
                if (payload.Contains("type:if"))
                    button.Content = "if (true)";
                else if (payload.Contains("type:for"))
                    button.Content = "for (int i...)";

                // This is the source of the drag-and-drop from the left sidebar
                DragDrop.DoDragDrop(button, payload, DragDropEffects.Copy);
            }
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                File.WriteAllText(_filePath, CodeEditor.Text);

                // This block now logs the activity for the specific user.
                if (_currentUser != null)
                {
                    RecentActivityService.AddOrUpdateActivity(_currentUser.Id, _filePath);
                }

                _mainFrame.Navigate(new HomePage(_mainFrame));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not save changes and go back: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RedrawGraph_Click(object sender, RoutedEventArgs e)
        {
            UpdateAstAndJson();
        }

        private async void GenerateCodeWithAI_Click(object sender, RoutedEventArgs e)
        {
            var button = sender as Button;
            if (button != null) button.IsEnabled = false;

            AiGeneratedCodeBox.Text = "Generating code with AI... Please wait.";
            try
            {
                string generatedCode = await GenerateCodeFromJson();
                AiGeneratedCodeBox.Text = generatedCode;
            }
            catch (Exception ex)
            {
                AiGeneratedCodeBox.Text = $"[Error] Could not generate code: {ex.Message}";
            }
            finally
            {
                if (button != null) button.IsEnabled = true;
            }
        }

        private async void SendMessage_Click(object sender, RoutedEventArgs e)
        {
            string userMessage = AiInputBox.Text;
            if (string.IsNullOrWhiteSpace(userMessage)) return;

            AiResponseBox.AppendText($"\n> You: {userMessage}\n");
            AiInputBox.Clear();

            try
            {
                // This is the general chat, using the simple SendToOllama
                string aiResponse = await SendToOllama(userMessage);
                AiResponseBox.AppendText($"\n> HeisenParse AI: {aiResponse}\n");
            }
            catch (Exception ex)
            {
                AiResponseBox.AppendText($"\n> [Error] Could not connect to AI: {ex.Message}\n");
            }
            AiResponseBox.ScrollToEnd();

            // NOTE: For Step 3 (Suggestion Buttons) integration, you would need to parse 
            // the AI's markdown response (e.g., "[Add If Block]") and trigger the 
            // node creation/insertion logic here based on that command.
        }

        // Node dragging and view panning are handled by GViewer's LayoutEditingEnabled = true
        private void ZoomIn_Click(object sender, RoutedEventArgs e) { if (_viewer.Graph != null) _viewer.ZoomF *= 1.2; }
        private void ZoomOut_Click(object sender, RoutedEventArgs e) { if (_viewer.Graph != null) _viewer.ZoomF /= 1.2; }
        private void ResetZoom_Click(object sender, RoutedEventArgs e) => _viewer.FitGraphBoundingBox();

        #endregion

        #region Helper and Ollama Methods

        private AstNode CreateAstNodeForType(string type)
        {
            if (string.IsNullOrEmpty(type)) return new AstNode("node", "Node", "");

            // The node structure is simplified here to represent a control flow element
            // Initial Label suggestions for Step 2
            if (type.Equals("if", StringComparison.OrdinalIgnoreCase))
            {
                string cond = Interaction.InputBox("Enter condition for if:", "If Condition", "a > 10");
                var node = new AstNode($"if ({cond})", "IfStatement", "IfStatement");
                var body = new AstNode("{ ... }", "Block", "Block");
                body.AddChild(new AstNode("// TODO: Then statement", "Statement", "Placeholder"));
                node.AddChild(body);
                return node;
            }
            if (type.Equals("for", StringComparison.OrdinalIgnoreCase))
            {
                string header = Interaction.InputBox("Enter for header:", "For Header", "int i = 0; i < 10; i++");
                var node = new AstNode($"for ({header})", "ForStatement", "ForStatement");
                var body = new AstNode("{ ... }", "Block", "Block");
                body.AddChild(new AstNode("// TODO: Loop body", "Statement", "Placeholder"));
                node.AddChild(body);
                return node;
            }
            if (type.Equals("while", StringComparison.OrdinalIgnoreCase))
            {
                string cond = Interaction.InputBox("Enter condition for while:", "While Condition", "true");
                var node = new AstNode($"while ({cond})", "WhileStatement", "WhileStatement");
                var body = new AstNode("{ ... }", "Block", "Block");
                body.AddChild(new AstNode("// TODO: Loop body", "Statement", "Placeholder"));
                node.AddChild(body);
                return node;
            }
            if (type.Equals("function", StringComparison.OrdinalIgnoreCase))
            {
                string name = Interaction.InputBox("Enter method name:", "Method Name", "NewMethod");
                var node = new AstNode($"{name}()", "Method", "MethodDeclaration");
                var body = new AstNode("{ ... }", "Block", "Block");
                node.AddChild(body);
                return node;
            }
            if (type.Equals("class", StringComparison.OrdinalIgnoreCase))
            {
                string name = Interaction.InputBox("Enter class name:", "Class Name", "NewClass");
                return new AstNode($"class {name}", "ClassDeclaration", "ClassDeclaration");
            }

            return new AstNode(type, "Custom", "Custom");
        }

        private async Task<string> GenerateCodeFromJson()
        {
            if (!File.Exists("ast.json")) return "[Error] ast.json not found. Edit the code or graph to generate it.";
            string json = await File.ReadAllTextAsync("ast.json");

            string prompt = $"Generate a complete and functional C# code file from the following AST JSON. Do not add any explanation, just the code.\n\n{json}";
            string generatedCode = await SendToOllama(prompt);

            if (generatedCode.StartsWith("```csharp"))
                generatedCode = generatedCode.Substring("```csharp".Length);
            if (generatedCode.EndsWith("```"))
                generatedCode = generatedCode.Substring(0, generatedCode.Length - "```".Length);

            return generatedCode.Trim();
        }

        private async Task<string> SendToOllama(string prompt)
        {
            var body = new { model = "llama3", prompt = prompt, stream = false };
            var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            HttpResponseMessage response = await httpClient.PostAsync($"http://localhost:{ollamaPort}/api/generate", content);
            response.EnsureSuccessStatusCode();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return doc.RootElement.GetProperty("response").GetString() ?? "[No response from AI]";
        }

        private async Task StartBundledOllamaIfNeededAsync()
        {
            try
            {
                if (Process.GetProcessesByName("ollama").Length > 0) return;

                string baseDir = AppDomain.CurrentDomain.BaseDirectory ?? Environment.CurrentDirectory;
                string bundledExe = Path.Combine(baseDir, "ollama", "ollama.exe");

                if (!File.Exists(bundledExe)) return;

                var psi = new ProcessStartInfo
                {
                    FileName = bundledExe,
                    Arguments = "serve",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };

                ollamaProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
                ollamaProcess.Start();
                startedBundledOllama = true;

                var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                await WaitForOllamaApiAsync(cts.Token);
            }
            catch (Exception ex)
            {
                AiResponseBox.AppendText($"\n> [Error] Could not start local Ollama server: {ex.Message}\n");
            }
        }

        private async Task<bool> WaitForOllamaApiAsync(CancellationToken cancellationToken)
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            string url = $"http://localhost:{ollamaPort}/";
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var resp = await client.GetAsync(url, cancellationToken);
                    if (resp.IsSuccessStatusCode) return true;
                }
                catch { /* Ignore connection errors while waiting */ }
                await Task.Delay(500, cancellationToken).ContinueWith(_ => { });
            }
            return false;
        }

        private void Page_Unloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (startedBundledOllama && ollamaProcess != null && !ollamaProcess.HasExited)
                {
                    if (!ollamaProcess.CloseMainWindow())
                        ollamaProcess.Kill(true);
                }
            }
            catch { /* Suppress errors on exit */ }
        }

        #endregion
    }
}