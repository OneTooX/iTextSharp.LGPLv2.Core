using System.IO;
using System.Text;
using iTextSharp.text;
using iTextSharp.text.exceptions;
using iTextSharp.text.pdf;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace iTextSharp.LGPLv2.Core.FunctionalTests;

[TestClass]
public class MarkedContentTests
{
    /// <summary>
    ///     BeginMarkedContentSequence(tag) passes a null property, which writes BMC. That opens a
    ///     sequence just as BDC does, so the depth has to be counted or the matching
    ///     EndMarkedContentSequence throws "Unbalanced begin/end marked content operators".
    /// </summary>
    [TestMethod]
    public void Verify_BeginMarkedContentSequence_WithoutProperty_CanBeEnded()
    {
        var pdfFilePath = TestUtils.GetOutputFileName();
        using (var fileStream = new FileStream(pdfFilePath, FileMode.Create))
        {
            using (var pdfDoc = new Document(PageSize.A4))
            {
                var pdfWriter = PdfWriter.GetInstance(pdfDoc, fileStream);

                pdfDoc.AddAuthor(TestUtils.Author);
                pdfDoc.Open();
                pdfDoc.Add(new Chunk("Real content"));

                var content = pdfWriter.DirectContent;
                content.BeginMarkedContentSequence(new PdfName("Artifact"));
                content.SetLineWidth(value: 1);
                content.MoveTo(x: 100, y: 100);
                content.LineTo(x: 200, y: 200);
                content.Stroke();
                content.EndMarkedContentSequence();

                // Throws if the depth is out of balance, which is what the missing count caused.
                content.SanityCheck();
            }
        }

        TestUtils.VerifyPdfFileIsReadable(pdfFilePath);
    }

    /// <summary>
    ///     An unbalanced sequence still has to be reported, so the fix must not silence the check.
    ///     The document is deliberately left open: closing it would raise the same error from
    ///     Document.Close and hide which call actually detected the imbalance.
    /// </summary>
    [TestMethod]
    public void Verify_UnclosedMarkedContentSequence_IsStillDetected()
    {
        var pdfDoc = new Document(PageSize.A4);
        using var stream = new MemoryStream();
        var pdfWriter = PdfWriter.GetInstance(pdfDoc, stream);
        pdfDoc.Open();

        var content = pdfWriter.DirectContent;
        content.BeginMarkedContentSequence(new PdfName("Artifact"));

        Assert.Throws<IllegalPdfSyntaxException>(() => content.SanityCheck());
    }

    /// <summary>The written content stream carries the BMC/EMC pair the structure relies on.</summary>
    [TestMethod]
    public void Verify_ArtifactMarkedContent_IsWrittenToTheContentStream()
    {
        var pdfDoc = new Document(PageSize.A4);
        using var stream = new MemoryStream();
        var pdfWriter = PdfWriter.GetInstance(pdfDoc, stream);
        pdfDoc.Open();

        var content = pdfWriter.DirectContent;
        content.BeginMarkedContentSequence(new PdfName("Artifact"));
        content.SetLineWidth(value: 1);
        content.MoveTo(x: 100, y: 100);
        content.LineTo(x: 200, y: 200);
        content.Stroke();
        content.EndMarkedContentSequence();

        var written = Encoding.ASCII.GetString(content.ToPdf(pdfWriter));
        StringAssert.Contains(written, "/Artifact BMC");
        StringAssert.Contains(written, "EMC");
    }
}
