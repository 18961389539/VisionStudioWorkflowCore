using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace VisionStudio.Scripting;

internal static partial class CSharpScriptRunner
{
    private sealed class LoopGuardRewriter : CSharpSyntaxRewriter
    {
        private static StatementSyntax Tick() =>
            SyntaxFactory.ParseStatement("global::VisionStudio.Scripting.ScriptExecutionContext.Tick();");

        private static BlockSyntax AddTick(BlockSyntax block) =>
            block.WithStatements(block.Statements.Insert(0, Tick()));

        private static StatementSyntax AddTick(StatementSyntax statement) =>
            statement is BlockSyntax block ? AddTick(block) : SyntaxFactory.Block(Tick(), statement);

        public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
        {
            var visited = (WhileStatementSyntax)base.VisitWhileStatement(node)!;
            return visited.WithStatement(AddTick(visited.Statement));
        }

        public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
        {
            var visited = (ForStatementSyntax)base.VisitForStatement(node)!;
            return visited.WithStatement(AddTick(visited.Statement));
        }

        public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
        {
            var visited = (ForEachStatementSyntax)base.VisitForEachStatement(node)!;
            return visited.WithStatement(AddTick(visited.Statement));
        }

        public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
        {
            var visited = (DoStatementSyntax)base.VisitDoStatement(node)!;
            return visited.WithStatement(AddTick(visited.Statement));
        }

        public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
        {
            var visited = (LocalFunctionStatementSyntax)base.VisitLocalFunctionStatement(node)!;
            return visited.Body is null ? visited : visited.WithBody(AddTick(visited.Body));
        }

        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
        {
            var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;
            return visited.Body is null ? visited : visited.WithBody(AddTick(visited.Body));
        }

        public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
        {
            var visited = (ConstructorDeclarationSyntax)base.VisitConstructorDeclaration(node)!;
            return visited.Body is null ? visited : visited.WithBody(AddTick(visited.Body));
        }

        public override SyntaxNode? VisitAccessorDeclaration(AccessorDeclarationSyntax node)
        {
            var visited = (AccessorDeclarationSyntax)base.VisitAccessorDeclaration(node)!;
            return visited.Body is null ? visited : visited.WithBody(AddTick(visited.Body));
        }
    }
}
