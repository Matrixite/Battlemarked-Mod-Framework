using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;

// Metadata-only: no game assembly is loaded or executed.
if (args.Length < 2 || args.Length > 3)
{
    Console.Error.WriteLine("Usage: InteropScanner <interop-folder> <output.txt> [regex]");
    return 2;
}
if (!Directory.Exists(args[0])) { Console.Error.WriteLine("Interop directory missing."); return 2; }
Regex filter;
try { filter = new Regex(args.Length == 3 ? args[2] : "card|hero|character|enemy|monster|dice|turn|action|campaign|quest|encounter|board|tile|inventory|loot|damage|health|ability|spell|session|network", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }
catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 2; }
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
using var writer = new StreamWriter(args[1], false, new UTF8Encoding(false));
writer.WriteLine("BMF interop inventory (metadata only; names are candidates, not validated hooks)");
writer.WriteLine($"Filter: {filter}");
int scanned = 0, failed = 0, matches = 0;
foreach (var path in Directory.EnumerateFiles(args[0], "*.dll").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
{
    try
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) continue;
        var md = pe.GetMetadataReader();
        if (!md.IsAssembly) continue;
        var assembly = md.GetAssemblyDefinition();
        writer.WriteLine($"\n=== {md.GetString(assembly.Name)} {assembly.Version} ===");
        scanned++;
        string FullName(TypeDefinitionHandle handle)
        {
            var t = md.GetTypeDefinition(handle);
            var name = md.GetString(t.Name);
            var parent = t.GetDeclaringType();
            if (!parent.IsNil) return FullName(parent) + "+" + name;
            var ns = md.GetString(t.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }
        foreach (var handle in md.TypeDefinitions)
        {
            var t = md.GetTypeDefinition(handle);
            var typeName = FullName(handle);
            var methods = t.GetMethods().Select(h => md.GetMethodDefinition(h)).ToArray();
            var typeMatches = filter.IsMatch(typeName);
            var selected = methods.Where(m => typeMatches || filter.IsMatch(md.GetString(m.Name))).ToArray();
            if (!typeMatches && selected.Length == 0) continue;
            matches++;
            writer.WriteLine($"TYPE {typeName}");
            foreach (var method in selected)
            {
                var parameters = method.GetParameters().Select(p => md.GetParameter(p))
                    .Where(p => p.SequenceNumber > 0).OrderBy(p => p.SequenceNumber)
                    .Select(p => md.GetString(p.Name));
                writer.WriteLine($"  METHOD {md.GetString(method.Name)}({string.Join(", ", parameters)}) sig={Convert.ToHexString(md.GetBlobBytes(method.Signature))} attrs={method.Attributes}");
            }
            if (typeMatches)
                foreach (var field in t.GetFields().Select(h => md.GetFieldDefinition(h)))
                    writer.WriteLine($"  FIELD {md.GetString(field.Name)} sig={Convert.ToHexString(md.GetBlobBytes(field.Signature))}");
        }
    }
    catch (Exception ex)
    {
        failed++;
        writer.WriteLine($"ERROR {Path.GetFileName(path)}: {ex.Message}");
    }
}
writer.WriteLine($"\nAssemblies scanned: {scanned}; errors: {failed}; matching types: {matches}");
Console.WriteLine($"Scanned {scanned} assemblies, {matches} matching types, {failed} errors. Output: {args[1]}");
return scanned == 0 || failed > 0 ? 1 : 0;
