// HeisenParserWPF/Models/AstNode.cs

using System.Collections.Generic;

namespace HeisenParserWPF.Models
{
    public class AstNode
    {
        // Properties used for visualization and serialization
        public string Label { get; set; }
        public string Type { get; set; }
        public string Kind { get; set; }

        // Structural hierarchy (AST children)
        public List<AstNode> Children { get; set; }

        // Flow control/Sequential connection (for graph drawing)
        public List<AstNode> SequentialSiblings { get; set; } = new List<AstNode>();

        // For Variable Lifetime Visualization
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
}