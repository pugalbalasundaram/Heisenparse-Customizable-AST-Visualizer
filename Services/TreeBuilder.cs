using HeisenParserWPF.Models; // Use the new model
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Linq;

namespace HeisenParserWPF.Services
{
    public interface ITreeBuilder
    {
        AstNode BuildTree(string code);
    }

    public class RoslynTreeBuilder : ITreeBuilder
    {
        public AstNode BuildTree(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
            {
                return new AstNode("Root", "CompilationUnit", "Empty");
            }
            var tree = CSharpSyntaxTree.ParseText(code);
            var root = tree.GetRoot();
            return BuildNodeRecursive(root) ?? new AstNode("Root", "CompilationUnit", "ParseFailed");
        }

        private AstNode? BuildNodeRecursive(SyntaxNode? node)
        {
            if (node == null) return null; // Handle null nodes gracefully

            if (node is CompilationUnitSyntax cu)
            {
                var rootNode = new AstNode("Root", node.GetType().Name, node.Kind().ToString());
                foreach (var member in cu.Members)
                    rootNode.AddChild(BuildNodeRecursive(member));
                foreach (var st in cu.DescendantNodes().OfType<GlobalStatementSyntax>())
                    rootNode.AddChild(BuildNodeRecursive(st.Statement));
                return rootNode;
            }
            if (node is NamespaceDeclarationSyntax ns)
            {
                var n = new AstNode($"namespace {ns.Name}", node.GetType().Name, node.Kind().ToString());
                foreach (var member in ns.Members)
                    n.AddChild(BuildNodeRecursive(member));
                return n;
            }
            if (node is ClassDeclarationSyntax cls)
            {
                var n = new AstNode($"class {cls.Identifier.Text}", node.GetType().Name, node.Kind().ToString());
                foreach (var member in cls.Members)
                    n.AddChild(BuildNodeRecursive(member));
                return n;
            }
            if (node is MethodDeclarationSyntax m)
            {
                var n = new AstNode($"{m.Identifier.Text}()", node.GetType().Name, node.Kind().ToString());
                foreach (var p in m.ParameterList.Parameters)
                    n.AddChild(new AstNode(p.Identifier.Text, p.GetType().Name, p.Kind().ToString()));
                if (m.Body != null)
                {
                    foreach (var stmt in m.Body.Statements)
                        n.AddChild(BuildNodeRecursive(stmt));
                }
                return n;
            }
            if (node is IfStatementSyntax ifs)
            {
                var n = new AstNode($"if ({Shorten(ifs.Condition?.ToString() ?? "")})", node.GetType().Name, node.Kind().ToString());
                n.AddChild(BuildNodeRecursive(ifs.Statement));
                if (ifs.Else?.Statement != null) n.AddChild(BuildNodeRecursive(ifs.Else.Statement));
                return n;
            }
            if (node is ExpressionStatementSyntax exprStmt)
            {
                return BuildExpressionNode(exprStmt.Expression);
            }

            // Fallback for other node types
            string codeStr = node.ToString().Trim();
            if (codeStr.Length > 60) codeStr = codeStr.Substring(0, 57) + "...";
            return new AstNode(codeStr.Replace("\r\n", " ").Replace("\n", " "), node.GetType().Name, node.Kind().ToString());
        }

        private AstNode BuildExpressionNode(ExpressionSyntax? expr)
        {
            if (expr == null) return new AstNode("<expr?>", "Expression", "");

            switch (expr)
            {
                case BinaryExpressionSyntax bin:
                    var binNode = new AstNode(Shorten(bin.OperatorToken.Text), bin.GetType().Name, bin.Kind().ToString());
                    binNode.AddChild(BuildExpressionNode(bin.Left));
                    binNode.AddChild(BuildExpressionNode(bin.Right));
                    return binNode;
                case InvocationExpressionSyntax inv:
                    var invNode = new AstNode($"{inv.Expression}()", inv.GetType().Name, inv.Kind().ToString());
                    if (inv.ArgumentList != null)
                        foreach (var arg in inv.ArgumentList.Arguments)
                            invNode.AddChild(BuildExpressionNode(arg.Expression));
                    return invNode;
                // Add other expression types as needed
                default:
                    string code = expr.ToString().Trim();
                    if (code.Length > 60) code = code.Substring(0, 57) + "...";
                    return new AstNode(code.Replace("\r\n", " ").Replace("\n", " "), expr.GetType().Name, expr.Kind().ToString());
            }
        }

        private string Shorten(string s, int max = 30) => s.Length > max ? s.Substring(0, max) + "…" : s;
    }
}