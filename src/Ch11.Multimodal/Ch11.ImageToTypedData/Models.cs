using System.ComponentModel;

namespace Ch11.ImageToTypedData;

// The shape we want back from the invoice image. These are the SAME typed-record
// + [Description] patterns from Chapter 7 (Structured Output) — the only new
// thing in Chapter 11 is that the input is a picture instead of text.
//
// [Description] flows into the generated JSON schema, so it doubles as the
// instruction to the model about what each field means.

public sealed record Invoice
{
    [Description("The invoice number or identifier, e.g. 'INV-2026-0454'.")]
    public string InvoiceNumber { get; init; } = "";

    [Description("The vendor or company that issued the invoice.")]
    public string Vendor { get; init; } = "";

    [Description("The invoice date as printed, ISO format (yyyy-MM-dd) if possible.")]
    public string InvoiceDate { get; init; } = "";

    [Description("The 3-letter currency code, e.g. 'USD', 'EUR'.")]
    public string Currency { get; init; } = "";

    [Description("The line items listed on the invoice.")]
    public List<InvoiceLine> LineItems { get; init; } = [];

    [Description("The grand total amount due, as a number (no currency symbol).")]
    public decimal Total { get; init; }
}

public sealed record InvoiceLine
{
    [Description("The description of the line item.")]
    public string Description { get; init; } = "";

    [Description("The quantity ordered.")]
    public int Quantity { get; init; }

    [Description("The line amount (quantity times unit price), as a number.")]
    public decimal Amount { get; init; }
}
