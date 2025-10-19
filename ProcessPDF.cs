using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using iText.Forms;
using iText.Forms.Fields;
using iText.IO.Font.Constants;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Layout;
using iText.Layout.Properties;

namespace CConquest.PDF;

public class ProcessPDF
{
    private readonly ILogger<ProcessPDF> _logger;

    public ProcessPDF(ILogger<ProcessPDF> logger)
    {
        _logger = logger;
    }

    [Function("ProcessPDF")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequest req)
    {
        if (HttpMethods.IsGet(req.Method))
        {
            return new OkObjectResult("Send a POST request with a JSON body to fill PDF form fields.");
        }

        if (!HttpMethods.IsPost(req.Method))
        {
            return new StatusCodeResult(StatusCodes.Status405MethodNotAllowed);
        }

        req.EnableBuffering();
        req.Body.Position = 0;

        PdfFillRequest? payload;
        try
        {
            using var reader = new StreamReader(req.Body);
            var body = await reader.ReadToEndAsync().ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(body))
            {
                return new BadRequestObjectResult("Request body cannot be empty.");
            }

            payload = JsonSerializer.Deserialize<PdfFillRequest>(body, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse request body.");
            return new BadRequestObjectResult("Request body is not valid JSON or does not match the expected schema.");
        }

        if (payload is null)
        {
            return new BadRequestObjectResult("Request body is missing required content.");
        }

        if (string.IsNullOrWhiteSpace(payload.PdfBase64))
        {
            return new BadRequestObjectResult("'pdfBase64' must contain the base64 encoded PDF.");
        }

        if (payload.Fields is null || payload.Fields.Count == 0)
        {
            return new BadRequestObjectResult("At least one field is required.");
        }

        var overlayEntries = new List<FieldOverlay>();
        var pageSizes = new List<Rectangle>();

        try
        {
            var pdfBytes = Convert.FromBase64String(payload.PdfBase64);

            using var inputStream = new MemoryStream(pdfBytes, writable: false);
            using var outputStream = new MemoryStream();
            using var pdfDocument = new PdfDocument(new PdfReader(inputStream), new PdfWriter(outputStream));
            
            var acroForm = PdfAcroForm.GetAcroForm(pdfDocument, false);
            if (acroForm is null)
            {
                return new BadRequestObjectResult("The supplied PDF does not contain any form fields to fill.");
            }

            var acroFormDictionary = acroForm.GetPdfObject();
            if (acroFormDictionary is null)
            {
                _logger.LogWarning("AcroForm dictionary is missing in the supplied PDF.");
                return new BadRequestObjectResult("The supplied PDF does not contain a valid form definition.");
            }

            if (acroFormDictionary.ContainsKey(XfaName))
            {
                _logger.LogWarning("Received an XFA-based form which is not supported by the current processing pipeline.");
                return new BadRequestObjectResult("XFA-based PDF forms are not supported. Please convert the template to a standard AcroForm PDF before submitting.");
            }

            var fieldsArray = acroFormDictionary.GetAsArray(PdfName.Fields);
            if (fieldsArray is null || fieldsArray.Size() == 0)
            {
                return new BadRequestObjectResult("The supplied PDF does not contain any form fields to fill.");
            }

            acroForm.SetGenerateAppearance(true);
            acroForm.SetNeedAppearances(false);
            acroFormDictionary.Remove(PdfName.NeedAppearances);

            var fieldsArrayNonNull = fieldsArray!;
            var fieldCount = fieldsArrayNonNull.Size();
            var fieldLookup = new Dictionary<string, (string OriginalName, PdfFormField Field)>(fieldCount, StringComparer.Ordinal);
            for (var i = 0; i < fieldCount; i++)
            {
                var fieldObject = fieldsArrayNonNull.Get(i);
                if (fieldObject is null)
                {
                    continue;
                }

                var resolvedObject = fieldObject.GetIndirectReference()?.GetRefersTo() ?? fieldObject;
                if (resolvedObject is not PdfDictionary fieldDictionary)
                {
                    continue;
                }

                var formField = PdfFormField.MakeFormField(fieldDictionary, pdfDocument);
                if (formField is null)
                {
                    continue;
                }

                RegisterField(formField, fieldLookup, pdfDocument, parentName: null);
            }

            _logger.LogDebug("Registered {FieldCount} form fields for processing.", fieldLookup.Count);

            foreach (var field in payload.Fields)
            {
                if (string.IsNullOrWhiteSpace(field.FieldName))
                {
                    _logger.LogWarning("Ignoring field with empty name.");
                    continue;
                }

                if (TryFindField(fieldLookup, field.FieldName, out var pdfField, out _))
                {
                    var value = field.Value ?? string.Empty;
                    pdfField.SetValue(value, true);
                    pdfField.RegenerateField();

                    if (payload.RenderTextOverlay)
                    {
                        CaptureFieldPlacement(pdfDocument, pdfField, value, overlayEntries);
                    }
                }
                else
                {
                    var available = string.Join(", ", fieldLookup.Values.Select(v => v.OriginalName).Distinct().Take(10));
                    _logger.LogWarning(
                        "Field '{FieldName}' was not found in the PDF template. First matches: {AvailableFields}",
                        field.FieldName,
                        available);
                }
            }

            if (payload.RenderTextOverlay)
            {
                for (var pageIndex = 1; pageIndex <= pdfDocument.GetNumberOfPages(); pageIndex++)
                {
                    pageSizes.Add(pdfDocument.GetPage(pageIndex).GetPageSize());
                }
            }

            acroForm.FlattenFields();
            pdfDocument.GetCatalog().Remove(PdfName.AcroForm);

            pdfDocument.Close();

            if (payload.RenderTextOverlay)
            {
                var overlayBytes = RenderTextOverlay(pageSizes, overlayEntries);
                var responsePayload = new PdfFillResponse(Convert.ToBase64String(overlayBytes));
                return new OkObjectResult(responsePayload);
            }
            else
            {
                var flattenedBytes = outputStream.ToArray();
                var responsePayload = new PdfFillResponse(Convert.ToBase64String(flattenedBytes));
                return new OkObjectResult(responsePayload);
            }
        }
        catch (FormatException ex)
        {
            _logger.LogWarning(ex, "Invalid base64 payload provided.");
            return new BadRequestObjectResult("'pdfBase64' is not a valid base64 encoded string.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unable to process PDF.");
            return new StatusCodeResult(StatusCodes.Status500InternalServerError);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static void CaptureFieldPlacement(PdfDocument pdfDocument, PdfFormField pdfField, string value, List<FieldOverlay> overlays)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        foreach (var widget in pdfField.GetWidgets())
        {
            var page = widget.GetPage();
            if (page is null)
            {
                continue;
            }

            var rectArray = widget.GetRectangle();
            if (rectArray is null)
            {
                continue;
            }

            var rectangle = ToRectangle(rectArray);
            if (rectangle is null)
            {
                continue;
            }

            var pageNumber = pdfDocument.GetPageNumber(page);
            var fontSize = pdfField.GetFontSize();
            if (fontSize <= 0)
            {
                fontSize = 10f;
            }

            overlays.Add(new FieldOverlay(pageNumber, rectangle, value, fontSize));
        }
    }

    private static byte[] RenderTextOverlay(IReadOnlyList<Rectangle> pageSizes, IReadOnlyList<FieldOverlay> overlays)
    {
        using var overlayStream = new MemoryStream();
        using (var overlayDocument = new PdfDocument(new PdfWriter(overlayStream)))
        {
            PdfFont font = PdfFontFactory.CreateFont(StandardFonts.COURIER);

            for (var index = 0; index < pageSizes.Count; index++)
            {
                var pageNumber = index + 1;
                var size = pageSizes[index];
                var page = overlayDocument.AddNewPage(new PageSize(size));

                using var canvas = new Canvas(new PdfCanvas(page), size);
                canvas.SetFont(font);

                foreach (var overlay in overlays.Where(o => o.PageNumber == pageNumber))
                {
                    canvas.SetFontSize(overlay.FontSize);
                    canvas.ShowTextAligned(
                        overlay.Text,
                        overlay.Rectangle.GetLeft(),
                        overlay.Rectangle.GetTop(),
                        TextAlignment.LEFT,
                        VerticalAlignment.TOP,
                        0);
                }
            }
        }

        return overlayStream.ToArray();
    }

    private static bool TryFindField(
        Dictionary<string, (string OriginalName, PdfFormField Field)> normalizedLookup,
        string fieldName,
        out PdfFormField pdfField,
        out string? matchedName)
    {
        var normalized = NormalizeFieldName(fieldName);
        if (normalizedLookup.TryGetValue(normalized, out var entry))
        {
            pdfField = entry.Field;
            matchedName = entry.OriginalName;
            return true;
        }

        var stripped = NormalizeFieldName(StripIndices(fieldName));
        if (!string.Equals(stripped, normalized, StringComparison.Ordinal) &&
            normalizedLookup.TryGetValue(stripped, out entry))
        {
            pdfField = entry.Field;
            matchedName = entry.OriginalName;
            return true;
        }

        pdfField = null!;
        matchedName = null;
        return false;
    }

    private static void RegisterField(
        PdfFormField formField,
        Dictionary<string, (string OriginalName, PdfFormField Field)> lookup,
        PdfDocument pdfDocument,
        string? parentName)
    {
        var fieldName = formField.GetFieldName()?.ToUnicodeString();
        var fullName = string.IsNullOrWhiteSpace(fieldName)
            ? parentName
            : parentName is null
                ? fieldName
                : string.Concat(parentName, ".", fieldName);

        if (!string.IsNullOrWhiteSpace(fullName))
        {
            var normalized = NormalizeFieldName(fullName);
            lookup[normalized] = (fullName, formField);

            var stripped = NormalizeFieldName(StripIndices(fullName));
            if (!string.Equals(stripped, normalized, StringComparison.Ordinal))
            {
                lookup.TryAdd(stripped, (fullName, formField));
            }
        }

        var kidsArray = formField.GetPdfObject().GetAsArray(PdfName.Kids);
        if (kidsArray is null)
        {
            return;
        }

        for (var i = 0; i < kidsArray.Size(); i++)
        {
            var childObject = kidsArray.Get(i);
            if (childObject is null)
            {
                continue;
            }

            var resolved = childObject.GetIndirectReference()?.GetRefersTo() ?? childObject;
            if (resolved is not PdfDictionary childDictionary)
            {
                continue;
            }

            var childField = PdfFormField.MakeFormField(childDictionary, pdfDocument);
            if (childField is null)
            {
                continue;
            }

            RegisterField(childField, lookup, pdfDocument, fullName);
        }
    }

    private static string NormalizeFieldName(string? name)
    {
        return (name ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static string StripIndices(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        return IndexPattern.Replace(name, string.Empty);
    }

    private static readonly Regex IndexPattern = new(@"\[\d+\]", RegexOptions.Compiled);
    private static readonly PdfName XfaName = new("XFA");

    private static Rectangle? ToRectangle(PdfArray rectArray)
    {
        if (rectArray.Size() < 4)
        {
            return null;
        }

        var left = rectArray.GetAsNumber(0)?.FloatValue() ?? 0f;
        var bottom = rectArray.GetAsNumber(1)?.FloatValue() ?? 0f;
        var right = rectArray.GetAsNumber(2)?.FloatValue() ?? left;
        var top = rectArray.GetAsNumber(3)?.FloatValue() ?? bottom;

        return new Rectangle(left, bottom, right - left, top - bottom);
    }

    private sealed record FieldOverlay(int PageNumber, Rectangle Rectangle, string Text, float FontSize);

    private sealed record PdfFillRequest(string PdfBase64, List<FormFieldValue>? Fields, bool RenderTextOverlay = false);

    private sealed record FormFieldValue(string FieldName, string? Value);

    private sealed record PdfFillResponse(string PdfBase64);
}
