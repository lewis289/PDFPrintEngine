# PDFPrintEngine

Azure Functions (.NET 8 isolated) HTTP trigger that accepts a base64 encoded, fillable PDF along with dynamic form field values, fills the form, flattens it (removing interactivity), and returns the resulting PDF as a base64 string.

## Requirements

- .NET 8 SDK
- Azure Functions Core Tools (for local execution)
- The function uses the [iText 7](https://itextpdf.com/) PDF library (AGPL licensed) and its Bouncy Castle adapter; review licensing terms before distributing compiled binaries.

## JSON Contract

```json
{
  "pdfBase64": "<base64 string of the fillable PDF>",
  "fields": [
    {
      "fieldName": "TextField1",
      "value": "Value to inject into the field"
    },
    {
      "fieldName": "CheckBox1",
      "value": "Yes"
    }
  ],
  "renderTextOverlay": false
}
```

- `pdfBase64` – required; base64 encoded PDF containing AcroForm fields.
- `fields` – required array; each element must include `fieldName` (exact field name from the PDF; matching is case-insensitive and you may omit array indices such as `[0]`) and an optional `value` string. Unmatched fields are ignored but logged.
- `renderTextOverlay` – optional boolean; when `true` the function returns a new blank PDF where each supplied value is drawn at the same coordinates as the original field. When `false` (default) the function returns the filled-and-flattened source PDF.

## Running Locally

```bash
# restore and build
dotnet build

# start the function host
func start
```

The `ProcessPDF` endpoint listens at `http://localhost:7071/api/ProcessPDF` by default.

### Sample Request

```bash
curl --request POST \
  --url http://localhost:7071/api/ProcessPDF \
  --header 'Content-Type: application/json' \
  --data '{
    "pdfBase64": "<base64 string>",
    "fields": [
      { "fieldName": "FullName", "value": "Ada Lovelace" },
      { "fieldName": "Date", "value": "2025-01-01" }
    ],
    "renderTextOverlay": true
  }'
```

### Response

```json
{
  "pdfBase64": "<base64 string of the flattened PDF>"
}
```

When `renderTextOverlay` is `false`, the payload is the filled-and-flattened original PDF; when `true`, the payload is a blank PDF with only the supplied values drawn in their original positions. On error, the function returns a descriptive `400` response for validation issues or `500` for unexpected failures. Fields missing from the PDF are skipped and noted in the function logs.

## Notes

- Flattening removes all interactivity, ensuring downstream consumers receive a static PDF.
- The function handles any PDF template that exposes standard AcroForm fields; XFA-based forms are rejected with a 400 response because the data lives in a different structure.
- Large PDFs may increase execution time; consider configuring the function timeout accordingly when deploying.

## Testing Tips

- Keep a known template with a few fields to validate the behavior locally.
- Compare the filled PDF visually or with a PDF text inspector to verify the flattened result.

## Credits

- Built in tandem with OpenAI's Codex assistant to accelerate PDF manipulation and documentation.
- Powered by [iText 7](https://itextpdf.com/) for PDF form handling and rendering.
