using System;
using System.IO;
using iTextSharp.text;
using iTextSharp.text.pdf;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace iTextSharp.LGPLv2.Core.FunctionalTests;

[TestClass]
public class DocumentInfoTests
{
    /// <summary>
    ///     Holds a character outside PDFDocEncoding, so it can only be stored as UTF-16 with a byte
    ///     order mark - which is what Word writes for any producer containing a non ASCII character.
    /// </summary>
    private const string UnicodeProducer = "Contoso π Publisher 2026";

    private const string Suffix = "; modified using ";

    /// <summary>
    ///     PdfStamperImp rebuilds /Producer rather than copying it, so it is the one entry of the
    ///     info dictionary that has to survive a decode and an encode.
    /// </summary>
    [TestMethod]
    public void Verify_UnicodeProducer_SurvivesStamping()
    {
        var producer = ReadProducer(Stamp(CreateWithProducer(UnicodeProducer)));

        StringAssert.StartsWith(producer, UnicodeProducer);
        StringAssert.Contains(producer, Suffix);
    }

    /// <summary>
    ///     The guard against appending the suffix twice compares the producer with Document.Product.
    ///     A producer decoded as NUL interleaved text never matches it.
    /// </summary>
    [TestMethod]
    public void Verify_StampingTwice_AppendsTheSuffixOnlyOnce()
    {
        var producer = ReadProducer(Stamp(Stamp(CreateWithProducer(UnicodeProducer))));

        Assert.AreEqual(expected: 1, Occurrences(producer, Suffix));
    }

    /// <summary>A producer that fits PDFDocEncoding must keep being written as plain bytes.</summary>
    [TestMethod]
    public void Verify_AsciiProducer_SurvivesStamping()
    {
        const string ascii = "Contoso Publisher 2026";

        var producer = ReadProducer(Stamp(CreateWithProducer(ascii)));

        StringAssert.StartsWith(producer, ascii);
        StringAssert.Contains(producer, Suffix);
    }

    private static byte[] CreateWithProducer(string producer)
    {
        using var stream = new MemoryStream();

        using (var document = new Document(PageSize.A4))
        {
            var writer = PdfWriter.GetInstance(document, stream);
            document.Open();
            document.Add(new Paragraph(str: "Real content"));

            // Document.AddProducer deliberately ignores its argument, so seed the dictionary itself.
            writer.Info.Put(PdfName.Producer, new PdfString(producer, PdfObject.TEXT_UNICODE));
        }

        return stream.ToArray();
    }

    private static byte[] Stamp(byte[] pdf)
    {
        using var output = new MemoryStream();

        using (var reader = new PdfReader(pdf))
        {
            using var stamper = new PdfStamper(reader, output);
            stamper.InsertPage(pageNumber: 1, PageSize.A4);
        }

        return output.ToArray();
    }

    private static string ReadProducer(byte[] pdf)
    {
        using var reader = new PdfReader(pdf);

        return reader.Info["Producer"];
    }

    private static int Occurrences(string text, string value)
    {
        var count = 0;

        for (var i = text.IndexOf(value, StringComparison.Ordinal);
             i >= 0;
             i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
