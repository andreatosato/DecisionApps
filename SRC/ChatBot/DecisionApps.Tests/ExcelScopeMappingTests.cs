using DecisionApps.ApiService.Services;

namespace DecisionApps.Tests;

public sealed class ExcelScopeMappingTests
{
    [Theory]
    [InlineData("Ambito AMA", "informativo")]
    [InlineData("Ambito ATAC", "informativo")]
    [InlineData("Ambito Informativo Portale", "informativo")]
    [InlineData("Ambito News", "informativo")]
    [InlineData("Ambito RSM", "informativo")]
    [InlineData("Ambito CRM", "crm")]
    [InlineData("Ambito Anagrafe", "anagrafe")]
    [InlineData("Ambito Scuola", "scuola")]
    [InlineData("Ambito TVA", "tva")]
    [InlineData(" \t ambito   inFORMATIVO \n PORTALE  ", "informativo")]
    [InlineData("Scuola", "scuola")]
    [InlineData("crm", "crm")]
    public void MapsConfirmedDomains(string scope, string expected) =>
        Assert.Equal(expected, ExcelScopeMapping.Resolve(scope));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ambito")]
    [InlineData("Ambito sconosciuto")]
    [InlineData("Ambito CRM extra")]
    [InlineData("Biglietti e abbonamenti")]
    [InlineData("Come apro una segnalazione CRM?")]
    public void LeavesUnknownScopesUnmapped(string? scope) =>
        Assert.Null(ExcelScopeMapping.Resolve(scope));
}
