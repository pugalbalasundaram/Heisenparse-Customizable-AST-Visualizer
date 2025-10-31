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
using Microsoft.Win32;
using Supabase.Gotrue;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

// --- DEDICATED CORE/MODEL NAMESPACE ---
// Fixes ambiguity errors by defining these structural types only once
namespace HeisenParserWPF.Core
{
    // Mock Services (Required for compilation when actual project files are missing)
    public class SupabaseClientManager { public static Client? Client; }
    public class RecentActivityService { public static void AddOrUpdateActivity(string userId, string path) { /* Mock */ } }
    public class Client { public Auth? Auth; }
    public class Auth { public User? CurrentUser; }

    #region AST Model and Tree Builder

    public class AstNode
    {
        public string Label { get; set; }
        public string Type { get; set; }
        public string Kind { get; set; }
        public List<AstNode> Children { get; set; }
        public List<AstNode> SequentialSiblings { get; set; } = new List<AstNode>();
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
            try
            {
                var tree = CSharpSyntaxTree.ParseText(code);
                var root = tree.GetRoot();
                return BuildNodeRecursive(root);
            }
            catch (Exception ex)
            {
                // Return a simple error node for visual editor, allowing drag/drop to continue
                return new AstNode($"Parsing Error: {ex.Message.Substring(0, Math.Min(ex.Message.Length, 50))}...", "Error", "Error");
            }
        }

        private AstNode BuildNodeRecursive(SyntaxNode node, List<string>? currentScopeVariables = null)
        {
            // Simplified logic adapted from original EditExistingCodePage for parsing basic structure
            currentScopeVariables ??= new List<string>();
            AstNode n = new AstNode(Shorten(node.ToString()), node.GetType().Name, node.Kind().ToString());
            n.AccessibleVariables = new List<string>(currentScopeVariables);

            // Handle block/method statements sequentially for flow visualization
            if (node is BlockSyntax block)
            {
                BuildSequentialStatements(n, block.Statements, new List<string>(currentScopeVariables));
            }
            else if (node is MethodDeclarationSyntax method && method.Body != null)
            {
                BuildSequentialStatements(n, method.Body.Statements, new List<string>(currentScopeVariables));
            }
            else
            {
                foreach (var childNode in node.ChildNodes())
                {
                    n.AddChild(BuildNodeRecursive(childNode, currentScopeVariables));
                }
            }
            return n;
        }

        private void BuildSequentialStatements(AstNode parentNode, System.Collections.Generic.IReadOnlyList<StatementSyntax> statements, List<string> currentScopeVariables)
        {
            AstNode? previousNode = null;
            foreach (var stmt in statements)
            {
                var currentNode = BuildNodeRecursive(stmt, currentScopeVariables);
                parentNode.AddChild(currentNode);

                if (previousNode != null)
                {
                    previousNode.AddSequentialSibling(currentNode);
                }
                previousNode = currentNode;
            }
        }

        private string Shorten(string s, int max = 30) => s.Length > max ? s.Substring(0, max) + "…" : s.Replace("\r\n", " ").Replace("\n", " ");
    }
    #endregion
}

namespace HeisenParserWPF.Pages
{
    // Use the classes defined in the Core namespace
    using HeisenParserWPF.Core;
    using Supabase.Gotrue; // Assuming User class is here or accessible via global usings

    public partial class NewCodePage : Page
    {
        private readonly Frame _mainFrame;
        private string? _filePath;
        private readonly GViewer _viewer;
        private readonly DispatcherTimer _updateTimer;
        private AstNode? rootAst;
        private readonly ITreeBuilder treeBuilder = new RoslynTreeBuilder();
        private readonly Dictionary<string, AstNode> graphIdToAst = new Dictionary<string, AstNode>();
        private static readonly HttpClient httpClient = new HttpClient();
        private Process? ollamaProcess;
        private bool startedBundledOllama = false;
        private readonly int ollamaPort = 11434;
        private readonly User? _currentUser;
        private int nodeCounter = 0; // NEW: To assign unique IDs to manually created nodes

        public NewCodePage(Frame mainFrame)
        {
            InitializeComponent();
            _mainFrame = mainFrame;
            // Use Core.SupabaseClientManager
            _currentUser = SupabaseClientManager.Client?.Auth?.CurrentUser;

            _viewer = new GViewer
            {
                Dock = System.Windows.Forms.DockStyle.Fill,
                ToolBarIsVisible = false,
                LayoutEditingEnabled = true,
                AllowDrop = true
            };

            _viewer.MouseDoubleClick += Viewer_MouseDoubleClick;
            _viewer.DragEnter += Viewer_DragEnter;
            _viewer.DragDrop += Viewer_DragDrop;
            GraphHost.Child = _viewer;

            _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
            _updateTimer.Tick += (s, e) => { _updateTimer.Stop(); UpdateAstAndJson(); };
            CodeEditor.TextChanged += (s, e) => { _updateTimer.Stop(); _updateTimer.Start(); };

            // Initialize the base structure
            CodeEditor.Text = "using System;\n\nnamespace MyProject\n{\n    class Program\n    {\n        static void Main(string[] args)\n        {\n            // Start adding nodes from the left!\n        }\n    }\n}";
            UpdateAstAndJson();
            _ = StartBundledOllamaIfNeededAsync();
        }

        #region Core Update and Visualization Logic

        private void UpdateAstAndJson()
        {
            try
            {
                rootAst = treeBuilder.BuildTree(CodeEditor.Text);
                PersistAstJson();
                if (rootAst != null)
                {
                    BuildAstGraphFromNode(rootAst);
                }
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
            Graph g = new Graph("AST")
            {
                Attr =
                {
                    LayerDirection = LayerDirection.TB,
                    NodeSeparation = 10,
                    MinNodeWidth = 50,
                    MinNodeHeight = 30
                }
            };

            AddNodeToGraph(g, root, null);

            // Draw Sequential Flow Edges
            foreach (var node in graphIdToAst.Values)
            {
                // Find the node ID by object reference
                string? currentId = graphIdToAst.FirstOrDefault(x => x.Value == node).Key;
                foreach (var sibling in node.SequentialSiblings)
                {
                    string? siblingId = graphIdToAst.FirstOrDefault(x => x.Value == sibling).Key;
                    if (!string.IsNullOrEmpty(currentId) && !string.IsNullOrEmpty(siblingId))
                    {
                        var edge = g.AddEdge(currentId, siblingId);
                        edge.Attr.ArrowheadAtTarget = ArrowStyle.Normal;
                        edge.Attr.Color = Microsoft.Msagl.Drawing.Color.DimGray;
                        edge.Attr.LineWidth = 2;
                    }
                }
            }

            _viewer.Graph = g;
        }

        private void AddNodeToGraph(Graph g, AstNode node, string? parentId)
        {
            string nodeId = Guid.NewGuid().ToString();
            var gNode = g.AddNode(nodeId);
            gNode.LabelText = node.Label;
            gNode.Id = nodeId;

            string kind = node.Kind ?? "";
            Microsoft.Msagl.Drawing.Color fillColor = Microsoft.Msagl.Drawing.Color.LightGray;

            if (kind.Contains("Method") || kind.Contains("Block")) fillColor = Microsoft.Msagl.Drawing.Color.LightGreen;
            else if (kind.Contains("If") || kind.Contains("For") || kind.Contains("While") || kind.Contains("DoWhile"))
            {
                gNode.Attr.Shape = Shape.Diamond;
                fillColor = Microsoft.Msagl.Drawing.Color.Orange;
            }
            else if (kind.Contains("Class") || kind.Contains("Namespace") || kind.Contains("CompilationUnit")) fillColor = Microsoft.Msagl.Drawing.Color.LightBlue;

            gNode.Attr.FillColor = fillColor;

            graphIdToAst[nodeId] = node;

            if (!string.IsNullOrEmpty(parentId))
            {
                var edge = g.AddEdge(parentId, nodeId);
                edge.Attr.ArrowheadAtTarget = ArrowStyle.Normal;
                edge.Attr.Color = Microsoft.Msagl.Drawing.Color.Gray;
            }

            foreach (var child in node.Children)
            {
                AddNodeToGraph(g, child, nodeId);
            }
        }

        #endregion

        #region Tree Editing Handlers (Smart Drag/Drop Logic)

        // Helper to find the container block/method of a given node (Searches recursively for a Block or Method)
        private AstNode? FindClosestBlockParent(AstNode? current, AstNode target)
        {
            if (current == null) return null;

            foreach (var child in current.Children)
            {
                if (child == target)
                {
                    // The parent of the target is the 'current' node.
                    // If 'current' is a block or method, return it. Otherwise, keep searching up.
                    if (current.Kind.Contains("Method") || current.Kind.Contains("Block"))
                    {
                        return current;
                    }
                    else
                    {
                        // To properly implement this outside of Roslyn, a full parent map is needed.
                        // For simplicity, we fallback to searching the full tree for the deepest block.
                        break;
                    }
                }

                var foundInChild = FindClosestBlockParent(child, target);
                if (foundInChild != null) return foundInChild;
            }
            return null;
        }


        private void Viewer_DragDrop(object? sender, System.Windows.Forms.DragEventArgs e)
        {
            try
            {
                if (e.Data == null || !e.Data.GetDataPresent(DataFormats.StringFormat)) return;
                string payload = (string?)e.Data.GetData(DataFormats.StringFormat) ?? "";
                string type = payload.StartsWith("type:") ? payload.Substring(5) : payload;

                // 1. Create the new node with a unique label
                AstNode newNode = CreateAstNodeForType(type);
                newNode.Label = $"{newNode.Label} ({++nodeCounter})";

                if (rootAst == null) rootAst = new AstNode("Root", "Root", "CompilationUnit");

                // 2. Determine potential target node (the one the user dropped on)
                var targetGNode = _viewer.ObjectUnderMouseCursor?.DrawingObject as Microsoft.Msagl.Drawing.Node;
                AstNode? targetAst = null;
                if (targetGNode != null && graphIdToAst.TryGetValue(targetGNode.Id, out var mapped))
                {
                    targetAst = mapped;
                }

                AstNode insertionParent = rootAst;

                // If dropping on an existing node, handle connection logic
                if (targetAst != null)
                {
                    // Check if the target is a container block/method
                    if (targetAst.Kind.Contains("Method") || targetAst.Kind.Contains("Block"))
                    {
                        insertionParent = targetAst;
                        targetAst.AddChild(newNode); // Add as a structural child (first item in sequence)
                    }
                    else
                    {
                        // Ask user for connection direction
                        string prompt =
                            $"The dropped node '{newNode.Label}' landed near '{targetAst.Label}'.\n" +
                            "How should it be connected?\n" +
                            "1. Sequentially (after) - Flow continues from target to new node.\n" +
                            "2. Structurally (as child) - New node runs inside target (e.g., inside an If block).\n" +
                            "3. Free Placement (no connection) - Insert into the closest scope and let the AI sort it.";

                        string result = Interaction.InputBox(prompt, "Select Node Connection", "1");

                        if (result == "1")
                        {
                            // Sequence: Find the block parent and append/sequence it
                            insertionParent = FindClosestBlockParent(rootAst, targetAst) ?? rootAst;
                            targetAst.AddSequentialSibling(newNode);
                            insertionParent.AddChild(newNode);
                        }
                        else if (result == "2")
                        {
                            // Structural Child: The new node is nested *inside* the target
                            targetAst.AddChild(newNode);
                            insertionParent = targetAst; // The parent is now the target node itself
                        }
                        else // Default to free placement / closest scope
                        {
                            // Find the deepest existing block (Main method body) and attach there
                            AstNode? deepestBlock = rootAst;
                            void DeepSearch(AstNode node)
                            {
                                if (node.Kind.Contains("Method") || node.Kind.Contains("Block"))
                                {
                                    deepestBlock = node;
                                }
                                foreach (var child in node.Children) DeepSearch(child);
                            }
                            DeepSearch(rootAst);
                            deepestBlock.AddChild(newNode);
                        }
                    }
                }
                else
                {
                    // Dropped on empty canvas, find the deepest block to insert
                    AstNode? deepestBlock = rootAst;
                    void DeepSearch(AstNode node)
                    {
                        if (node.Kind.Contains("Method") || node.Kind.Contains("Block"))
                        {
                            deepestBlock = node;
                        }
                        foreach (var child in node.Children) DeepSearch(child);
                    }
                    DeepSearch(rootAst);
                    deepestBlock.AddChild(newNode);
                }

                // 3. Persist and redraw
                PersistAstJson();
                BuildAstGraphFromNode(rootAst);

                // 4. Update the code editor by generating code from the new AST
                string generatedCode = Task.Run(() => GenerateCodeFromJson()).GetAwaiter().GetResult();
                CodeEditor.Text = generatedCode;
                UpdateAstAndJson();

            }
            catch (Exception ex)
            {
                MessageBox.Show("Error while dropping node: " + ex.Message);
            }
        }

        #endregion

        #region UI Event Handlers (Buttons, Zoom)

        private void Snippet_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is Button button && button.Tag is string payload)
            {
                // Dynamic content for better UX (similar to EditExistingCodePage)
                if (payload.Contains("type:if")) button.Content = "if (true)";
                else if (payload.Contains("type:for")) button.Content = "for (int i...)";

                DragDrop.DoDragDrop(button, payload, DragDropEffects.Copy);
            }
        }

        private void Back_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(CodeEditor.Text))
            {
                _mainFrame.Navigate(new HomePage(_mainFrame));
                return;
            }

            MessageBoxResult result = MessageBox.Show(
                "Do you want to save changes to your new file?",
                "HeisenParser",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                SaveFileDialog saveFileDialog = new SaveFileDialog
                {
                    Filter = "C# File (*.cs)|*.cs|All files (*.*)|*.*",
                    Title = "Save New Code File",
                    FileName = "NewCode.cs"
                };

                if (saveFileDialog.ShowDialog() == true)
                {
                    try
                    {
                        _filePath = saveFileDialog.FileName;
                        File.WriteAllText(_filePath, CodeEditor.Text);

                        if (_currentUser != null && !string.IsNullOrEmpty(_filePath))
                        {
                            RecentActivityService.AddOrUpdateActivity(_currentUser.Id, _filePath);
                        }

                        _mainFrame.Navigate(new HomePage(_mainFrame));
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Could not save file: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
            else if (result == MessageBoxResult.No)
            {
                _mainFrame.Navigate(new HomePage(_mainFrame));
            }
        }

        private void RedrawGraph_Click(object sender, RoutedEventArgs e)
        {
            UpdateAstAndJson();
        }

        private async void GenerateCodeWithAI_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button button) return;
            button.IsEnabled = false;

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
                button.IsEnabled = true;
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
                string aiResponse = await SendToOllama(userMessage);
                AiResponseBox.AppendText($"\n> HeisenParse AI: {aiResponse}\n");
            }
            catch (Exception ex)
            {
                AiResponseBox.AppendText($"\n> [Error] Could not connect to AI: {ex.Message}\n");
            }
            AiResponseBox.ScrollToEnd();
        }

        private void Viewer_MouseDoubleClick(object? sender, System.Windows.Forms.MouseEventArgs e)
        {
            try
            {
                var obj = _viewer.ObjectUnderMouseCursor;
                if (obj?.DrawingObject is Microsoft.Msagl.Drawing.Node gNode)
                {
                    string current = gNode.LabelText ?? "";
                    string newLabel = Interaction.InputBox("Edit node label:", "Edit Node", current);
                    if (!string.IsNullOrEmpty(newLabel) && newLabel != current)
                    {
                        gNode.LabelText = newLabel;
                        if (!string.IsNullOrEmpty(gNode.Id) && graphIdToAst.TryGetValue(gNode.Id, out var astNode))
                        {
                            astNode.Label = newLabel;
                            PersistAstJson();
                        }
                        _viewer.Invalidate();
                    }
                }
            }
            catch { /* Suppress errors */ }
        }

        private void Viewer_DragEnter(object? sender, System.Windows.Forms.DragEventArgs e)
        {
            if (e.Data != null && e.Data.GetDataPresent(DataFormats.StringFormat))
                e.Effect = System.Windows.Forms.DragDropEffects.Copy;
            else
                e.Effect = System.Windows.Forms.DragDropEffects.None;
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs e) { if (_viewer.Graph != null) _viewer.ZoomF *= 1.2; }
        private void ZoomOut_Click(object sender, RoutedEventArgs e) { if (_viewer.Graph != null) _viewer.ZoomF /= 1.2; }
        private void ResetZoom_Click(object sender, RoutedEventArgs e) => _viewer.FitGraphBoundingBox();

        #endregion

        #region Helper and Ollama Methods

        private AstNode CreateAstNodeForType(string type)
        {
            // Implementation moved here from the core logic to be callable within the page
            if (string.IsNullOrEmpty(type)) return new AstNode("node", "Node", "");

            string label;
            string kind;
            switch (type.ToLower())
            {
                case "if":
                    label = Interaction.InputBox("Enter condition for if:", "If Condition", "a > 10");
                    kind = "IfStatement";
                    var ifNode = new AstNode($"if ({label})", kind, kind);
                    var ifBody = new AstNode("{ ... }", "Block", "Block");
                    ifBody.AddChild(new AstNode("// TODO: If body", "Statement", "Placeholder"));
                    ifNode.AddChild(ifBody);
                    return ifNode;

                case "for":
                    label = Interaction.InputBox("Enter for header:", "For Header", "int i = 0; i < 10; i++");
                    kind = "ForStatement";
                    var forNode = new AstNode($"for ({label})", kind, kind);
                    var forBody = new AstNode("{ ... }", "Block", "Block");
                    forBody.AddChild(new AstNode("// TODO: Loop body", "Statement", "Placeholder"));
                    forNode.AddChild(forBody);
                    return forNode;

                case "while":
                    label = Interaction.InputBox("Enter condition for while:", "While Condition", "true");
                    kind = "WhileStatement";
                    var whileNode = new AstNode($"while ({label})", kind, kind);
                    var whileBody = new AstNode("{ ... }", "Block", "Block");
                    whileBody.AddChild(new AstNode("// TODO: Loop body", "Statement", "Placeholder"));
                    whileNode.AddChild(whileBody);
                    return whileNode;

                case "do/while":
                    label = Interaction.InputBox("Enter condition for do/while:", "Do/While Condition", "running");
                    kind = "DoWhileStatement";
                    var doWhileNode = new AstNode($"do/while ({label})", kind, kind);
                    var doWhileBody = new AstNode("{ ... }", "Block", "Block");
                    doWhileBody.AddChild(new AstNode("// TODO: Do body", "Statement", "Placeholder"));
                    doWhileNode.AddChild(doWhileBody);
                    return doWhileNode;

                case "try/catch":
                    label = "try / catch";
                    kind = "TryStatement";
                    var tryNode = new AstNode(label, kind, kind);
                    var tryBlock = new AstNode("try { ... }", "Block", "Block");
                    tryBlock.AddChild(new AstNode("// TODO: Try body", "Statement", "Placeholder"));
                    var catchBlock = new AstNode("catch { ... }", "Block", "Block");
                    catchBlock.AddChild(new AstNode("// TODO: Catch body", "Statement", "Placeholder"));
                    tryNode.AddChild(tryBlock);
                    tryNode.AddChild(catchBlock);
                    return tryNode;

                case "function":
                    label = Interaction.InputBox("Enter method name:", "Method Name", "NewMethod");
                    kind = "MethodDeclaration";
                    var funcNode = new AstNode($"{label}()", kind, kind);
                    var funcBody = new AstNode("{ ... }", "Block", "Block");
                    funcNode.AddChild(funcBody);
                    return funcNode;

                case "class":
                    label = Interaction.InputBox("Enter class name:", "Class Name", "NewClass");
                    kind = "ClassDeclaration";
                    return new AstNode($"class {label}", kind, kind);

                case "assign":
                    label = Interaction.InputBox("Enter assignment statement:", "Assignment", "int x = 0;");
                    kind = "LocalDeclarationStatement";
                    return new AstNode(label, kind, kind);

                case "console.write":
                    label = Interaction.InputBox("Enter Console.WriteLine content:", "Console Output", "\"Hello World\"");
                    kind = "InvocationExpression";
                    return new AstNode($"Console.WriteLine({label});", kind, kind);

                case "return":
                    label = Interaction.InputBox("Enter return value (or leave blank):", "Return Statement", "");
                    kind = "ReturnStatement";
                    return new AstNode($"return {label};", kind, kind);

                default:
                    label = Interaction.InputBox($"Enter label for {type}:", "Custom Node", type);
                    kind = "Custom";
                    return new AstNode(label, kind, kind);
            }
        }

        private async Task<string> GenerateCodeFromJson()
        {
            if (!File.Exists("ast.json")) return "[Error] ast.json not found. Edit the code or graph to generate it.";
            string json = await File.ReadAllTextAsync("ast.json");

            string prompt = $"Generate a complete and functional C# code file from the following AST JSON. IMPORTANT: Convert nodes with kind 'Placeholder' or label starting with '// TODO' into actual, simple C# statements that logically fit the flow (e.g., 'Console.WriteLine(\"...\");' or 'i++;'). Do not add any explanation, just the code. Use standard indentation.\n\n{json}";
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
                if (Process.GetProcessesByName("ollama").Any()) return;

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