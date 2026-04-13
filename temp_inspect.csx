using System;
using System.Linq;
using System.Reflection;
var asm = Assembly.LoadFrom(@"C:\Users\asafmahlev\.nuget\packages\avalonia.htmlrenderer\11.3.0\lib\net10.0\Avalonia.HtmlRenderer.dll");
var type = asm.GetTypes().First(t => t.Name == "HtmlPanel");
foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(x => x.Name))
    Console.WriteLine($"P: {p.Name} ({p.PropertyType.Name})");
foreach (var m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).OrderBy(x => x.Name))
    Console.WriteLine($"M: {m.Name}");
