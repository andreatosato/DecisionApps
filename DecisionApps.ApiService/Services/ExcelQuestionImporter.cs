using System.IO.Compression;
using ClosedXML.Excel;
using DecisionApps.Contracts;

namespace DecisionApps.ApiService.Services;

public sealed class ExcelQuestionImporter
{
    public ImportResult Import(Stream stream, string fileName, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(Path.GetExtension(fileName), ".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Caricare un file Excel .xlsx.");
        }

        if (stream.Length > SimulatorLimits.MaxFileBytes)
        {
            throw new InvalidDataException("Il file supera il limite di 10 MB.");
        }

        using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true))
        {
            if (zip.Entries.Count > 1_024 || zip.Entries.Sum(e => e.Length) > 50 * 1024 * 1024)
            {
                throw new InvalidDataException("Il contenuto decompresso del file Excel supera i limiti consentiti.");
            }
        }

        stream.Position = 0;
        using var workbook = new XLWorkbook(stream);
        var questions = new List<ImportedQuestion>();
        foreach (var sheet in workbook.Worksheets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var header = sheet.RowsUsed().Take(30)
                .SelectMany(row => row.CellsUsed())
                .Where(cell => !cell.HasFormula && IsHeader(cell.GetString())).ToArray();
            if (header.Length == 0)
            {
                continue;
            }
            if (header.Length != 1)
            {
                throw new InvalidDataException($"Il foglio '{sheet.Name}' contiene piu colonne DOMANDE/DOMANDA.");
            }

            var headerCells = header[0].WorksheetRow().CellsUsed().Where(cell => !cell.HasFormula).ToArray();
            var originHeaders = headerCells.Where(cell => ExcelScopeMapping.Normalize(cell.GetString()) == "FOGLIO ORIGINE").ToArray();
            var scopeHeaders = headerCells.Where(cell => ExcelScopeMapping.Normalize(cell.GetString()) == "AMBITO").ToArray();
            var referenceHeaders = originHeaders.Length > 0 ? originHeaders : scopeHeaders;
            if (referenceHeaders.Length > 1)
            {
                throw new InvalidDataException($"Il foglio '{sheet.Name}' contiene piu colonne per l'ambito di riferimento.");
            }
            var scopeHeader = referenceHeaders.SingleOrDefault();

            foreach (var row in sheet.RowsUsed().Where(r => r.RowNumber() > header[0].Address.RowNumber))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var cell = row.Cell(header[0].Address.ColumnNumber);
                if (cell.HasFormula)
                {
                    throw new InvalidDataException($"La domanda in '{sheet.Name}', riga {row.RowNumber()}, contiene una formula.");
                }
                var question = cell.GetString().Trim();
                if (question.Length == 0)
                {
                    continue;
                }
                if (question.Length > SimulatorLimits.MaxQuestionLength)
                {
                    throw new InvalidDataException($"Domanda troppo lunga in '{sheet.Name}', riga {row.RowNumber()} (massimo 6000 caratteri).");
                }
                if (questions.Count >= SimulatorLimits.MaxQuestions)
                {
                    throw new InvalidDataException("Il file supera il limite di 10000 domande.");
                }
                string? scope = null;
                string? scopeSource = null;
                if (scopeHeader is not null)
                {
                    var scopeCell = row.Cell(scopeHeader.Address.ColumnNumber);
                    if (scopeCell.HasFormula)
                    {
                        throw new InvalidDataException($"L'ambito in '{sheet.Name}', riga {row.RowNumber()}, contiene una formula.");
                    }
                    var value = scopeCell.GetString().Trim();
                    scope = value.Length == 0 ? null : value;
                    scopeSource = scopeHeader.GetString().Trim();
                }
                else if (ExcelScopeMapping.Resolve(sheet.Name) is not null)
                {
                    scope = sheet.Name;
                    scopeSource = "Nome foglio";
                }

                questions.Add(new ImportedQuestion(questions.Count + 1, sheet.Name, row.RowNumber(), question,
                    scope, ExcelScopeMapping.Resolve(scope), scopeSource));
            }
        }

        if (questions.Count == 0)
        {
            throw new InvalidDataException("Nessuna domanda trovata. Inserire una colonna DOMANDE (o DOMANDA) nelle prime 30 righe di un foglio.");
        }

        return new ImportResult(Path.GetFileName(fileName), questions);
    }

    private static bool IsHeader(string text) =>
        text.Trim().Equals("DOMANDE", StringComparison.OrdinalIgnoreCase) ||
        text.Trim().Equals("DOMANDA", StringComparison.OrdinalIgnoreCase);
}
