namespace TaxCertificate.Application.Parsers;

/// <summary>Logical fields the parser knows how to locate on a vergi levhası.</summary>
public enum TaxCertificateField
{
    Vkn,
    Tckn,
    TicaretUnvani,
    AdiSoyadi,
    VergiDairesi,
    IseBaslamaTarihi,
    AnaFaaliyetKodu,
    AnaFaaliyetAciklamasi,
    Adres,

    /// <summary>
    /// Recognised but not extracted. These exist purely as boundaries: knowing that
    /// "VERGİ TÜRÜ" is a label stops the address collector from walking into its value.
    /// </summary>
    Boundary,
}
