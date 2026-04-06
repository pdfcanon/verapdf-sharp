using PdfLexer;
using PdfLexer.DOM;
using VeraPdfSharp.Core;
using VeraPdfSharp.Model;
using VeraPdfSharp.Validation;

var file = args[0];
var flavour = PDFAFlavour.PDFA2B;

using var parser = PdfLexerValidationParser.FromFile(file, flavour);
var root = parser.Root;

void DumpTree(IModelObject obj, int depth = 0) {
    var pad = new string(' ', depth*2);
    Console.WriteLine($"{pad}{obj.ObjectType}");
    if (obj.ObjectType.Contains("DeviceN")) {
        foreach (var prop in obj.GetPropertyNames()) {
            Console.WriteLine($"{pad}  .{prop} = {obj.GetPropertyValue(prop)}");
        }
    }
    foreach (var link in obj.GetLinkNames()) {
        foreach (var child in obj.GetLinkedObjects(link)) {
            DumpTree(child, depth+1);
        }
    }
}

DumpTree(root);
