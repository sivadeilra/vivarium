using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Vivarium;

/// <summary>
/// Uses Roslyn to extract the public symbols exported by a Vivarium source file.
/// Runs at define-time and stores results in the @exports: header metadata.
/// </summary>
public static class SymbolExtractor
{
    /// <summary>
    /// Parse a C# source string and return a list of exported public symbol names.
    /// Includes: classes, records, interfaces, enums, top-level methods/functions.
    /// Format: "ClassName", "ClassName.MethodName()", "MethodName()" for top-level.
    /// </summary>
    public static List<string> ExtractExports(string source)
    {
        var exports = new List<string>();
        try
        {
            var tree = CSharpSyntaxTree.ParseText(source);
            var root = tree.GetCompilationUnitRoot();

            // Top-level type declarations
            foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                // Only include top-level types (not nested) for brevity
                if (type.Parent is TypeDeclarationSyntax) continue;

                var isPublic = type.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword));
                // In scripting mode, types without explicit access modifier are accessible
                if (!isPublic && type.Modifiers.Any(m =>
                    m.IsKind(SyntaxKind.PrivateKeyword) || m.IsKind(SyntaxKind.InternalKeyword)))
                    continue;

                exports.Add(type.Identifier.Text);

                // Public methods of the type (skip constructors, operators)
                foreach (var method in type.Members.OfType<MethodDeclarationSyntax>())
                {
                    var mPublic = method.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword));
                    if (!mPublic) continue;
                    var pCount = method.ParameterList.Parameters.Count;
                    var pSuffix = pCount == 0 ? "()" : $"({pCount})";
                    exports.Add($"{type.Identifier.Text}.{method.Identifier.Text}{pSuffix}");
                }
            }

            // Top-level method declarations (scripting-style global methods)
            foreach (var method in root.Members.OfType<GlobalStatementSyntax>()
                .Select(g => g.Statement)
                .OfType<LocalFunctionStatementSyntax>())
            {
                exports.Add($"{method.Identifier.Text}()");
            }
        }
        catch
        {
            // If parsing fails, return empty — error will surface at eval time
        }
        return exports;
    }
}
