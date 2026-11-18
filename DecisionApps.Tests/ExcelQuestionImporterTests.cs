using System.IO.Compression;
using ClosedXML.Excel;
using DecisionApps.ApiService.Services;
using DecisionApps.Contracts;

namespace DecisionApps.Tests;

public sealed class ExcelQuestionImporterTests
{
    private readonly ExcelQuestionImporter importer = new();

    [Theory]
    [InlineData("DOMANDE")]
    [InlineData("Domanda")]
    [InlineData("  domande  ")]
    public void FindsHeadersAndPreservesSheetRowAndDuplicates(string header)
    {
        using var workbook = new XLWorkbook();
        workbook.AddWorksheet("Copertina").Cell("A1").Value = "Riepilogo";
        var first = workbook.AddWorksheet("Storico");
        first.Cell("M3").Value = header;
        first.Cell("M4").Value = "  Dove si trova il municipio?  ";
        first.Cell("M6").Value = "Dove si trova il municipio?";
        first.Cell("N5").Value = "Questa non e una domanda";
        var second = workbook.AddWorksheet("Campione");
        second.Cell("B1").Value = "Domanda";
        second.Cell("B2").Value = "Vorrei un certificato";
        using var stream = Save(workbook);

        var result = importer.Import(stream, "domande.xlsx");
        Assert.Equal(3, result.Questions.Count);
        Assert.Equal(new ImportedQuestion(1, "Storico", 4, "Dove si trova il municipio?"), result.Questions[0]);
        Assert.Equal(6, result.Questions[1].Row);
        Assert.Equal("Campione", result.Questions[2].Sheet);
        Assert.Equal(3, result.Questions[2].Id);
    }

    [Fact]
    public void RejectsMissingHeader()
    {
        using var workbook = new XLWorkbook();
        workbook.AddWorksheet("Test").Cell("A1").Value = "Titolo";
        using var stream = Save(workbook);
        Assert.Throws<InvalidDataException>(() => importer.Import(stream, "test.xlsx"));
    }

    [Fact]
    public void RejectsEmptyQuestionColumn()
    {
        using var workbook = new XLWorkbook();
        workbook.AddWorksheet("Test").Cell("A1").Value = "DOMANDE";
        using var stream = Save(workbook);
        Assert.Throws<InvalidDataException>(() => importer.Import(stream, "test.xlsx"));
    }

    [Fact]
    public void RejectsAmbiguousHeaders()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Test");
        sheet.Cell("A1").Value = "DOMANDE";
        sheet.Cell("B1").Value = "DOMANDA";
        sheet.Cell("A2").Value = "Una domanda";
        using var stream = Save(workbook);
        Assert.Throws<InvalidDataException>(() => importer.Import(stream, "test.xlsx"));
    }

    [Theory]
    [InlineData("test.xls")]
    [InlineData("test.csv")]
    [InlineData("test.txt")]
    public void RejectsUnsupportedExtension(string fileName)
    {
        using var stream = new MemoryStream();
        Assert.Throws<InvalidDataException>(() => importer.Import(stream, fileName));
    }

    [Fact]
    public void RejectsCorruptWorkbook()
    {
        using var stream = new MemoryStream([1, 2, 3]);
        Assert.Throws<InvalidDataException>(() => importer.Import(stream, "test.xlsx"));
    }

    [Theory]
    [InlineData(6000, true)]
    [InlineData(6001, false)]
    public void EnforcesExactQuestionLengthLimit(int length, bool valid)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Test");
        sheet.Cell("A1").Value = "DOMANDE";
        sheet.Cell("A2").Value = new string('a', length);
        using var stream = Save(workbook);
        if (valid) Assert.Single(importer.Import(stream, "test.xlsx").Questions);
        else Assert.Throws<InvalidDataException>(() => importer.Import(stream, "test.xlsx"));
    }

    [Fact]
    public void RejectsFormulasInsteadOfEvaluatingThem()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Test");
        sheet.Cell("A1").Value = "DOMANDE";
        sheet.Cell("A2").FormulaA1 = "\"Domanda da formula\"";
        using var stream = Save(workbook);
        Assert.Throws<InvalidDataException>(() => importer.Import(stream, "test.xlsx"));
    }

    [Fact]
    public void EnforcesFileSizeLimit()
    {
        using var stream = new MemoryStream(new byte[SimulatorLimits.MaxFileBytes + 1]);
        Assert.Throws<InvalidDataException>(() => importer.Import(stream, "test.xlsx"));
    }

    [Fact]
    public void RejectsOversizedUncompressedArchive()
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            using var entry = zip.CreateEntry("large.xml", CompressionLevel.Fastest).Open();
            var chunk = new byte[1024 * 1024];
            for (var i = 0; i < 51; i++) entry.Write(chunk);
        }
        stream.Position = 0;
        Assert.Throws<InvalidDataException>(() => importer.Import(stream, "test.xlsx"));
    }

    [Theory]
    [InlineData(10000, true)]
    [InlineData(10001, false)]
    public void EnforcesExactQuestionCountLimit(int count, bool valid)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Test");
        sheet.Cell("A1").Value = "DOMANDE";
        sheet.Cell("A2").InsertData(Enumerable.Repeat("Una domanda", count));
        using var stream = Save(workbook);
        if (valid) Assert.Equal(count, importer.Import(stream, "test.xlsx").Questions.Count);
        else Assert.Throws<InvalidDataException>(() => importer.Import(stream, "test.xlsx"));
    }

    [Fact]
    public void PrefersSourceSheetLabelOverScopeSubtopicOrQuestion()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Storico");
        sheet.Cell("A1").Value = "Foglio origine";
        sheet.Cell("E1").Value = "Ambito";
        sheet.Cell("M1").Value = "Domanda";
        sheet.Cell("A2").Value = "Ambito ATAC";
        sheet.Cell("E2").Value = "Tempo reale";
        sheet.Cell("M2").Value = "Quando arriva il prossimo bus?";
        using var stream = Save(workbook);

        var question = Assert.Single(importer.Import(stream, "test.xlsx").Questions);
        Assert.Equal("Ambito ATAC", question.Scope);
        Assert.Equal("Foglio origine", question.ScopeSource);
        Assert.Equal("informativo", question.ExpectedAgent);
        Assert.Equal("Quando arriva il prossimo bus?", question.Question);
    }

    [Fact]
    public void ImportsSourceScopeFromSampleWithOnlySourceAndQuestion()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Campione 200");
        sheet.Cell("A1").Value = " \tFOGLIO   ORIGINE ";
        sheet.Cell("B1").Value = "Domanda";
        sheet.Cell("A2").Value = "  Ambito Scuola ";
        sheet.Cell("B2").Value = "Qual e il menu?";
        using var stream = Save(workbook);

        var question = Assert.Single(importer.Import(stream, "test.xlsx").Questions);
        Assert.Equal("Ambito Scuola", question.Scope);
        Assert.Equal("scuola", question.ExpectedAgent);
    }

    [Fact]
    public void FallsBackToScopeColumnWhenNoSourceColumnExists()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Test");
        sheet.Cell("D4").Value = "AMBITO";
        sheet.Cell("F4").Value = "DOMANDE";
        sheet.Cell("D5").Value = "Ambito CRM";
        sheet.Cell("F5").Value = "Dove si trova il Colosseo?";
        using var stream = Save(workbook);

        var question = Assert.Single(importer.Import(stream, "test.xlsx").Questions);
        Assert.Equal("crm", question.ExpectedAgent);
        Assert.Equal("AMBITO", question.ScopeSource);
        Assert.Equal("Ambito CRM", question.Scope);
    }

    [Theory]
    [InlineData("Ambito Anagrafe", "anagrafe")]
    [InlineData("Ambito TVA", "tva")]
    public void UsesKnownSheetNameOnlyWhenNoScopeColumnsExist(string sheetName, string expected)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet(sheetName);
        sheet.Cell("A1").Value = "DOMANDE";
        sheet.Cell("A2").Value = "Una domanda";
        using var stream = Save(workbook);

        var question = Assert.Single(importer.Import(stream, "test.xlsx").Questions);
        Assert.Equal(sheetName, question.Scope);
        Assert.Equal("Nome foglio", question.ScopeSource);
        Assert.Equal(expected, question.ExpectedAgent);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ambito sconosciuto")]
    [InlineData("Consultazione CRM generica")]
    public void PreservesUnknownOrMissingScopeWithoutInferringFromOtherColumns(string value)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Ambito CRM");
        sheet.Cell("A1").Value = "Foglio origine";
        sheet.Cell("B1").Value = "Ambito";
        sheet.Cell("C1").Value = "Domanda";
        sheet.Cell("A2").Value = "Ambito Scuola";
        sheet.Cell("B2").Value = "Scuola";
        sheet.Cell("C2").Value = "Prima domanda";
        sheet.Cell("A3").Value = value;
        sheet.Cell("B3").Value = "CRM";
        sheet.Cell("C3").Value = "Vorrei aprire una segnalazione";
        using var stream = Save(workbook);

        var questions = importer.Import(stream, "test.xlsx").Questions;
        Assert.Equal("scuola", questions[0].ExpectedAgent);
        Assert.Equal(value.Length == 0 ? null : value, questions[1].Scope);
        Assert.Null(questions[1].ExpectedAgent);
        Assert.Equal("Foglio origine", questions[1].ScopeSource);
    }

    [Fact]
    public void KeepsLegacyQuestionOnlyImportsUnlabelled()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Test");
        sheet.Cell("A1").Value = "DOMANDE";
        sheet.Cell("A2").Value = "Vorrei un certificato";
        using var stream = Save(workbook);
        var question = Assert.Single(importer.Import(stream, "test.xlsx").Questions);
        Assert.Null(question.Scope);
        Assert.Null(question.ExpectedAgent);
        Assert.Null(question.ScopeSource);
    }

    [Theory]
    [InlineData("Foglio origine")]
    [InlineData("Ambito")]
    public void RejectsDuplicateReferenceColumns(string header)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Test");
        sheet.Cell("A1").Value = header;
        sheet.Cell("B1").Value = header;
        sheet.Cell("C1").Value = "DOMANDE";
        sheet.Cell("C2").Value = "Una domanda";
        using var stream = Save(workbook);
        Assert.Throws<InvalidDataException>(() => importer.Import(stream, "test.xlsx"));
    }

    [Fact]
    public void RejectsScopeFormula()
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Test");
        sheet.Cell("A1").Value = "Foglio origine";
        sheet.Cell("B1").Value = "DOMANDE";
        sheet.Cell("A2").FormulaA1 = "\"Ambito CRM\"";
        sheet.Cell("B2").Value = "Una domanda";
        using var stream = Save(workbook);
        Assert.Throws<InvalidDataException>(() => importer.Import(stream, "test.xlsx"));
    }

    internal static MemoryStream Save(XLWorkbook workbook)
    {
        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }
}
