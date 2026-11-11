using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

// Dumps a canonical, diffable description of an assembly: type names, member signatures,
// per-method IL hashes, and the user-string heap. Two dumps being identical means the
// compiled code is identical, regardless of source comments or line numbers.
//
// Built to answer one question: did translating comments change anything but comments?

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: AsmDiff <assembly> <output.txt>");
    return 2;
}

var assemblyPath = args[0];
var outputPath = args[1];

using var stream = File.OpenRead(assemblyPath);
using var peReader = new PEReader(stream);
var reader = peReader.GetMetadataReader();

var lines = new List<string>();

// Types and their members.
foreach (var typeHandle in reader.TypeDefinitions)
{
    var type = reader.GetTypeDefinition(typeHandle);
    var typeName = reader.GetString(type.Name);
    var typeNamespace = reader.GetString(type.Namespace);
    var fullName = string.IsNullOrEmpty(typeNamespace) ? typeName : $"{typeNamespace}.{typeName}";

    lines.Add($"TYPE {fullName}");

    foreach (var methodHandle in type.GetMethods())
    {
        var method = reader.GetMethodDefinition(methodHandle);
        var methodName = reader.GetString(method.Name);
        var signature = reader.GetBlobBytes(method.Signature);
        var signatureHash = Convert.ToHexString(SHA256.HashData(signature))[..16];

        // Hash the IL body. This is the decisive part: any change to executable code shows
        // up here even when no name or string changed.
        var ilHash = "no-body";
        if (method.RelativeVirtualAddress != 0)
        {
            var body = peReader.GetMethodBody(method.RelativeVirtualAddress);
            ilHash = Convert.ToHexString(SHA256.HashData(body.GetILContent().ToArray()))[..16];
        }

        lines.Add($"  METHOD {methodName} sig={signatureHash} il={ilHash}");
    }

    foreach (var fieldHandle in type.GetFields())
    {
        var field = reader.GetFieldDefinition(fieldHandle);
        lines.Add($"  FIELD {reader.GetString(field.Name)}");
    }

    foreach (var propertyHandle in type.GetProperties())
    {
        var property = reader.GetPropertyDefinition(propertyHandle);
        lines.Add($"  PROPERTY {reader.GetString(property.Name)}");
    }
}

// Every string literal the assembly carries. User-visible text lives here, so a damaged
// literal is caught even if it happens to sit in an untested code path.
var strings = new List<string>();
var stringHandle = MetadataTokens.UserStringHandle(0);

while (true)
{
    stringHandle = reader.GetNextHandle(stringHandle);
    if (stringHandle.IsNil)
    {
        break;
    }

    var value = reader.GetUserString(stringHandle);
    if (!string.IsNullOrEmpty(value))
    {
        strings.Add(value);
    }
}

strings.Sort(StringComparer.Ordinal);
lines.Add("STRINGS");
lines.AddRange(strings.Select(s => $"  \"{s}\""));

File.WriteAllLines(outputPath, lines, new UTF8Encoding(false));
Console.WriteLine($"{Path.GetFileName(assemblyPath)}: {lines.Count} lines -> {outputPath}");
return 0;