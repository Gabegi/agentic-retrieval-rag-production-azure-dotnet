using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace RagApp.UnitTests;

// PdfCleaner's Win1252Strict field calls Encoding.GetEncoding(1252, ...), which throws
// NotSupportedException unless CodePagesEncodingProvider is registered first. The real
// app registers it once in program.cs at startup (Functions host) - dotnet test never
// runs that file, so the test process needs its own registration, once per run, before
// any test touches PdfCleaner.
[TestClass]
public static class AssemblyInitialize
{
    [AssemblyInitialize]
    public static void RegisterEncodingProviders(TestContext context) =>
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
}
