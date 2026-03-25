using System.Text;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Vivarium;

/// <summary>
/// Wraps a Roslyn CSharpScript incremental scripting session.
/// Each evaluation builds on the prior state (variables, types, usings persist).
/// </summary>
public sealed class ScriptingEngine
{
    private ScriptState<object>? _state;
    private readonly ScriptOptions _baseOptions;
    private readonly object _lock = new();

    public ScriptingEngine()
    {
        _baseOptions = ScriptOptions.Default
            .AddReferences(
                typeof(object).Assembly,                  // System.Runtime
                typeof(Console).Assembly,                 // System.Console
                typeof(Enumerable).Assembly,              // System.Linq
                typeof(List<>).Assembly,                  // System.Collections.Generic
                typeof(System.Text.Json.JsonSerializer).Assembly,
                typeof(System.Net.Http.HttpClient).Assembly,
                typeof(StringBuilder).Assembly,
                typeof(System.IO.File).Assembly,
                typeof(System.Text.RegularExpressions.Regex).Assembly
            )
            .AddImports(
                "System",
                "System.Collections.Generic",
                "System.IO",
                "System.Linq",
                "System.Text",
                "System.Text.Json",
                "System.Text.RegularExpressions",
                "System.Threading.Tasks"
            );
    }

    /// <summary>
    /// Evaluate C# code in the incremental session.
    /// Returns structured result with stdout capture, return value, and error info.
    /// </summary>
    public async Task<EvalResult> EvalAsync(string code, int timeoutMs = 30000)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            ScriptState<object> newState;
            var oldOut = Console.Out;
            var oldErr = Console.Error;

            try
            {
                Console.SetOut(stdout);
                Console.SetError(stderr);

                using var cts = new CancellationTokenSource(timeoutMs);

                if (_state == null)
                {
                    newState = await CSharpScript.RunAsync<object>(
                        code,
                        _baseOptions,
                        cancellationToken: cts.Token);
                }
                else
                {
                    newState = await _state.ContinueWithAsync<object>(
                        code,
                        _baseOptions,
                        cancellationToken: cts.Token);
                }
            }
            finally
            {
                Console.SetOut(oldOut);
                Console.SetError(oldErr);
            }

            sw.Stop();
            _state = newState;

            string? returnValue = null;
            if (newState.ReturnValue != null)
            {
                returnValue = FormatValue(newState.ReturnValue);
            }

            return new EvalResult
            {
                Success = true,
                Stdout = stdout.ToString(),
                Stderr = stderr.ToString(),
                ReturnValue = returnValue,
                ReturnType = newState.ReturnValue?.GetType()?.FullName,
                DurationMs = sw.ElapsedMilliseconds
            };
        }
        catch (CompilationErrorException ex)
        {
            sw.Stop();
            return new EvalResult
            {
                Success = false,
                Stdout = stdout.ToString(),
                Stderr = stderr.ToString(),
                Error = string.Join("\n", ex.Diagnostics),
                DurationMs = sw.ElapsedMilliseconds
            };
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            return new EvalResult
            {
                Success = false,
                Stdout = stdout.ToString(),
                Stderr = stderr.ToString(),
                Error = $"Execution timed out after {timeoutMs}ms",
                DurationMs = sw.ElapsedMilliseconds
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new EvalResult
            {
                Success = false,
                Stdout = stdout.ToString(),
                Stderr = stderr.ToString(),
                Error = ex.ToString(),
                DurationMs = sw.ElapsedMilliseconds
            };
        }
    }

    /// <summary>
    /// Get all variables currently in the scripting session.
    /// </summary>
    public List<VariableInfo> GetVariables(string? filter = null)
    {
        if (_state == null)
            return [];

        var vars = _state.Variables
            .Where(v => !v.Name.StartsWith("<"))  // skip compiler-generated
            .Select(v => new VariableInfo
            {
                Name = v.Name,
                Type = PrettyTypeName(v.Type),
                ValueShort = TruncateRepr(FormatValue(v.Value), 120)
            });

        if (!string.IsNullOrEmpty(filter))
        {
            vars = vars.Where(v =>
                v.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                v.Type.Contains(filter, StringComparison.OrdinalIgnoreCase));
        }

        return vars.ToList();
    }

    /// <summary>
    /// Deep-inspect a single variable by name.
    /// </summary>
    public VariableDetail? InspectVariable(string name)
    {
        if (_state == null) return null;

        var v = _state.Variables.FirstOrDefault(x => x.Name == name);
        if (v == null) return null;

        var detail = new VariableDetail
        {
            Name = v.Name,
            Type = PrettyTypeName(v.Type),
            Value = FormatValue(v.Value)
        };

        if (v.Value != null)
        {
            var type = v.Value.GetType();
            detail.Members = type.GetMembers(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
                .Where(m => m.MemberType is System.Reflection.MemberTypes.Property
                    or System.Reflection.MemberTypes.Method
                    or System.Reflection.MemberTypes.Field)
                .Select(m => $"{m.MemberType}: {m.Name}")
                .Take(50)
                .ToList();

            if (v.Value is Delegate d)
            {
                detail.Docstring = d.Method.ToString();
            }
        }

        return detail;
    }

    /// <summary>
    /// Reset the session, clearing all state. Returns the options for re-bootstrap.
    /// </summary>
    public void Reset()
    {
        _state = null;
    }

    private static string FormatValue(object? value)
    {
        if (value == null) return "null";
        try
        {
            return value.ToString() ?? "null";
        }
        catch
        {
            return $"<{value.GetType().Name}: ToString() failed>";
        }
    }

    private static string TruncateRepr(string s, int maxLen)
    {
        if (s.Length <= maxLen) return s;
        return s[..(maxLen - 3)] + "...";
    }

    private static string PrettyTypeName(Type type)
    {
        if (!type.IsGenericType)
            return type.FullName ?? type.Name;

        var baseName = type.Name;
        var tickIndex = baseName.IndexOf('`');
        if (tickIndex > 0) baseName = baseName[..tickIndex];

        // Use short namespace for well-known system types
        var ns = type.Namespace;
        if (ns != null && (ns.StartsWith("System.Collections") || ns == "System"))
            baseName = baseName; // just the short name
        else if (ns != null)
            baseName = ns + "." + baseName;

        var args = type.GetGenericArguments().Select(PrettyTypeName);
        return $"{baseName}<{string.Join(", ", args)}>";
    }
}

public class EvalResult
{
    public bool Success { get; set; }
    public string? Stdout { get; set; }
    public string? Stderr { get; set; }
    public string? ReturnValue { get; set; }
    public string? ReturnType { get; set; }
    public string? Error { get; set; }
    public long DurationMs { get; set; }

    public override string ToString()
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrEmpty(Stdout))
            sb.AppendLine($"[stdout]\n{Stdout.TrimEnd()}");
        if (!string.IsNullOrEmpty(Stderr))
            sb.AppendLine($"[stderr]\n{Stderr.TrimEnd()}");
        if (Success)
        {
            if (ReturnValue != null)
                sb.AppendLine($"[result: {ReturnType}] {ReturnValue}");
            else
                sb.AppendLine("[ok]");
        }
        else
        {
            sb.AppendLine($"[error] {Error}");
        }
        sb.Append($"({DurationMs}ms)");
        return sb.ToString();
    }
}

public class VariableInfo
{
    public required string Name { get; set; }
    public required string Type { get; set; }
    public required string ValueShort { get; set; }
}

public class VariableDetail
{
    public required string Name { get; set; }
    public required string Type { get; set; }
    public required string Value { get; set; }
    public List<string>? Members { get; set; }
    public string? Docstring { get; set; }
}
