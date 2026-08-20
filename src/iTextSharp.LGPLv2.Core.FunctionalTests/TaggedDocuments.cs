using System.Collections.Generic;
using System.IO;
using iTextSharp.text;
using iTextSharp.text.pdf;

namespace iTextSharp.LGPLv2.Core.FunctionalTests;

/// <summary>
///     Builds the small tagged documents the structure tests work on, and reads the trees back out
///     again. Built with the library rather than kept as files, so the tests carry no binary
///     fixtures and say in code exactly what they assume.
/// </summary>
internal static class TaggedDocuments
{
    /// <summary>
    ///     A tagged document with one paragraph per page, each holding one marked content sequence -
    ///     the smallest thing that can show a structure tree being kept, cut down or merged.
    /// </summary>
    public static byte[] Create(int pages, string label = "Page")
    {
        using var output = new MemoryStream();

        using (var document = new Document(PageSize.A4))
        {
            var writer = PdfWriter.GetInstance(document, output);
            writer.CloseStream = false;
            writer.SetTagged();
            document.AddAuthor(TestUtils.Author);
            document.Open();

            var root = new PdfStructureElement(writer.StructureTreeRoot, new PdfName(name: "Document"));
            var font = BaseFont.CreateFont();

            for (var page = 1; page <= pages; page++)
            {
                var paragraph = new PdfStructureElement(root, PdfName.P);
                var content = writer.DirectContent;
                content.BeginMarkedContentSequence(paragraph);
                content.BeginText();
                content.SetFontAndSize(font, size: 12);
                content.SetTextMatrix(x: 50, y: 700);
                content.ShowText($"{label} {page}");
                content.EndText();
                content.EndMarkedContentSequence();

                if (page < pages)
                {
                    writer.PageEmpty = false;
                    document.NewPage();
                }
            }
        }

        return output.ToArray();
    }

    public static byte[] Untagged(int pages)
    {
        using var output = new MemoryStream();

        using (var document = new Document(PageSize.A4))
        {
            var writer = PdfWriter.GetInstance(document, output);
            writer.CloseStream = false;
            document.Open();

            for (var page = 1; page <= pages; page++)
            {
                document.Add(new Paragraph($"Untagged {page}"));

                if (page < pages)
                {
                    document.NewPage();
                }
            }
        }

        return output.ToArray();
    }

    public static PdfDictionary StructTreeRoot(PdfReader reader) => reader.Catalog.GetAsDict(PdfName.Structtreeroot);

    /// <summary>Every element below the root carrying the given role.</summary>
    public static List<PdfDictionary> ElementsWithRole(PdfReader reader, PdfName role)
    {
        var found = new List<PdfDictionary>();
        var structTreeRoot = StructTreeRoot(reader);

        if (structTreeRoot != null)
        {
            Walk(structTreeRoot.Get(PdfName.K), role, found);
        }

        return found;
    }

    /// <summary>
    ///     The pages the tree points at, as object numbers, so a test can ask whether every reference
    ///     names a page the document still has.
    /// </summary>
    public static List<int> PagesReferenced(PdfReader reader)
    {
        var pages = new List<int>();

        foreach (var element in ElementsWithRole(reader, PdfName.P))
        {
            if (element.Get(PdfName.Pg) is PrIndirectReference page)
            {
                pages.Add(page.Number);
            }
        }

        return pages;
    }

    public static List<int> PageObjectNumbers(PdfReader reader)
    {
        var pages = new List<int>();

        for (var page = 1; page <= reader.NumberOfPages; page++)
        {
            pages.Add(reader.GetPageOrigRef(page).Number);
        }

        return pages;
    }

    /// <summary>The keys of a flat /Nums number tree, which is the shape both writers produce.</summary>
    public static List<int> ParentTreeKeys(PdfReader reader)
    {
        var keys = new List<int>();
        var nums = StructTreeRoot(reader)?.GetAsDict(PdfName.Parenttree)?.GetAsArray(PdfName.Nums);

        if (nums == null)
        {
            return keys;
        }

        for (var index = 0; index < nums.Size; index += 2)
        {
            keys.Add(nums.GetAsNumber(index).IntValue);
        }

        return keys;
    }

    /// <summary>Writes the reader out through the stamper and reads it back, as a caller would.</summary>
    public static PdfReader RoundTrip(PdfReader reader)
    {
        using var output = new MemoryStream();
        var stamper = new PdfStamper(reader, output);
        stamper.Close();
        reader.Close();

        return new PdfReader(output.ToArray());
    }

    private static void Walk(PdfObject node, PdfName role, List<PdfDictionary> found)
    {
        if (PdfReader.GetPdfObject(node) is PdfArray array)
        {
            foreach (var kid in array.ArrayList)
            {
                Walk(kid, role, found);
            }

            return;
        }

        if (PdfReader.GetPdfObject(node) is not PdfDictionary element)
        {
            return;
        }

        if (role.Equals(element.Get(PdfName.S)))
        {
            found.Add(element);
        }

        if (element.Get(PdfName.K) != null)
        {
            Walk(element.Get(PdfName.K), role, found);
        }
    }
}
