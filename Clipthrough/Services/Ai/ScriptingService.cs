using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Clipthrough.Services;

public sealed class ScriptingService : IScriptingService
{
    private static readonly ScriptOptions DefaultOptions = ScriptOptions.Default
        .AddReferences(
            typeof(object).Assembly,
            typeof(Enumerable).Assembly,
            typeof(Regex).Assembly,
            typeof(JsonDocument).Assembly,
            typeof(Encoding).Assembly)
        .AddImports(
            "System",
            "System.Collections.Generic",
            "System.Linq",
            "System.Text",
            "System.Text.Json",
            "System.Text.RegularExpressions");

    public sealed class ScriptGlobals
    {
        public string Input { get; init; } = string.Empty;
    }

    public async Task<string> EvaluateAsync(string code, string input, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return input;
        }

        var globals = new ScriptGlobals { Input = input ?? string.Empty };
        var result = await CSharpScript.EvaluateAsync<object?>(code, DefaultOptions, globals, typeof(ScriptGlobals), cancellationToken).ConfigureAwait(false);
        return result switch
        {
            null => string.Empty,
            string s => s,
            _ => result.ToString() ?? string.Empty,
        };
    }

    public static IReadOnlyList<Models.UserScript> GetDefaultScripts() => new[]
    {
        new Models.UserScript { Name = "JSON quote", Code = "JsonSerializer.Serialize(Input)" },
        new Models.UserScript { Name = "JSON unquote", Code = "JsonSerializer.Deserialize<string>(Input) ?? string.Empty" },
        new Models.UserScript { Name = "JSON minify", Code = "JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(Input))" },
        new Models.UserScript { Name = "JSON pretty", Code = "JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(Input), new JsonSerializerOptions { WriteIndented = true })" },
        new Models.UserScript { Name = "URL encode", Code = "Uri.EscapeDataString(Input)" },
        new Models.UserScript { Name = "URL decode", Code = "Uri.UnescapeDataString(Input)" },
        new Models.UserScript { Name = "Base64 encode", Code = "Convert.ToBase64String(Encoding.UTF8.GetBytes(Input))" },
        new Models.UserScript { Name = "Base64 decode", Code = "Encoding.UTF8.GetString(Convert.FromBase64String(Input))" },
        new Models.UserScript { Name = "Collapse whitespace", Code = "Regex.Replace(Input, @\"\\s+\", \" \").Trim()" },
        new Models.UserScript { Name = "Reverse lines", Code = "string.Join(Environment.NewLine, Input.Split(new[]{'\\n'}).Reverse().Select(l => l.TrimEnd('\\r')))" },
    };
}
