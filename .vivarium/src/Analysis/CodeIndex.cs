//@VIVARIUM@
//@description: Roslyn-based code index: parse a C# codebase once, query it many times

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

public class CodeIndex
{
    public record MethodInfo(string File, string ClassName, string MethodName,
        string ReturnType, string[] Parameters, bool IsAsync, bool IsPublic, int Line);

    public record ClassInfo(string File, string Name, string Kind,
        string? BaseType, string[] Interfaces, int Line);

    public record PropertyInfo(string File, string ClassName, string Name, string Type, bool IsPublic);

    public List<ClassInfo> Classes { get; } = [];
    public List<MethodInfo> Methods { get; } = [];
    public List<PropertyInfo> Properties { get; } = [];
    public List<string> Files { get; } = [];

    public static CodeIndex Load(string rootPath, string searchPattern = "*.cs")
    {
        var idx = new CodeIndex();
        foreach (var file in Directory.EnumerateFiles(rootPath, searchPattern, SearchOption.AllDirectories))
        {
            idx.Files.Add(file);
            var text = File.ReadAllText(file);
            var tree = CSharpSyntaxTree.ParseText(text, path: file);
            var root = tree.GetCompilationUnitRoot();
            var rel = Path.GetRelativePath(rootPath, file).Replace('\\', '/');

            foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var loc = type.GetLocation().GetLineSpan();
                var baseList = type.BaseList?.Types.Select(t => t.ToString()).ToArray() ?? [];
                idx.Classes.Add(new ClassInfo(
                    File: rel,
                    Name: type.Identifier.Text,
                    Kind: type.Keyword.Text,
                    BaseType: baseList.FirstOrDefault(),
                    Interfaces: baseList.Skip(1).ToArray(),
                    Line: loc.StartLinePosition.Line + 1));

                foreach (var method in type.Members.OfType<MethodDeclarationSyntax>())
                {
                    var mLoc = method.GetLocation().GetLineSpan();
                    var parms = method.ParameterList.Parameters
                        .Select(p => $"{p.Type} {p.Identifier}").ToArray();
                    idx.Methods.Add(new MethodInfo(
                        File: rel,
                        ClassName: type.Identifier.Text,
                        MethodName: method.Identifier.Text,
                        ReturnType: method.ReturnType.ToString(),
                        Parameters: parms,
                        IsAsync: method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)),
                        IsPublic: method.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)),
                        Line: mLoc.StartLinePosition.Line + 1));
                }

                foreach (var prop in type.Members.OfType<PropertyDeclarationSyntax>())
                {
                    idx.Properties.Add(new PropertyInfo(
                        File: rel,
                        ClassName: type.Identifier.Text,
                        Name: prop.Identifier.Text,
                        Type: prop.Type.ToString(),
                        IsPublic: prop.Modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword))));
                }
            }
        }
        return idx;
    }

    public void PrintSummary()
    {
        Console.WriteLine($"Files:      {Files.Count}");
        Console.WriteLine($"Types:      {Classes.Count}  ({Classes.Count(c => c.Kind == "class")} class, {Classes.Count(c => c.Kind == "record")} record, {Classes.Count(c => c.Kind == "interface")} interface)");
        Console.WriteLine($"Methods:    {Methods.Count}  ({Methods.Count(m => m.IsAsync)} async, {Methods.Count(m => m.IsPublic)} public)");
        Console.WriteLine($"Properties: {Properties.Count}");
    }
}