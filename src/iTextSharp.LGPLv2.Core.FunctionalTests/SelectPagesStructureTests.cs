using System.Collections.Generic;
using System.IO;
using iTextSharp.text;
using iTextSharp.text.pdf;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace iTextSharp.LGPLv2.Core.FunctionalTests;

/// <summary>
///     SelectPages used to leave the structure tree describing pages the document no longer had.
///     Every count still looked healthy - the elements were all there, the marked content ids were
///     all there - while half of them pointed at content a reader could never reach.
/// </summary>
[TestClass]
public class SelectPagesStructureTests
{
    /// <summary>Each page of the fixture carries one paragraph with one marked content id.</summary>
    private const int PagesInFixture = 3;

    [TestMethod]
    public void Verify_SelectPages_KeepsTheStructureOfTheKeptPage()
    {
        var reader = SelectFrom(TaggedDocument(), pagesToKeep: new[] { 1 });

        Assert.AreEqual(expected: 1, reader.NumberOfPages);
        var structTreeRoot = reader.Catalog.GetAsDict(PdfName.Structtreeroot);
        Assert.IsNotNull(structTreeRoot, message: "the structure tree was lost");
        Assert.AreEqual(expected: 1, Paragraphs(reader).Count, message: "the kept page lost its paragraph");
    }

    [TestMethod]
    public void Verify_SelectPages_DropsTheStructureOfTheRemovedPages()
    {
        var whole = new PdfReader(TaggedDocument());
        Assert.AreEqual(PagesInFixture, Paragraphs(whole).Count, message: "the fixture is not what the test assumes");

        var reader = SelectFrom(TaggedDocument(), pagesToKeep: new[] { 2 });

        Assert.AreEqual(expected: 1, Paragraphs(reader).Count,
            message: "the elements of the dropped pages are still in the tree");
    }

    /// <summary>
    ///     The point of the exercise: no structure element may reference a page that is gone. This
    ///     is what an accessibility checker reports as an orphaned tag.
    /// </summary>
    [TestMethod]
    public void Verify_SelectPages_LeavesNoTagPointingAtADroppedPage()
    {
        var reader = SelectFrom(TaggedDocument(), pagesToKeep: new[] { 3 });

        var pages = new HashSet<int>();

        for (var page = 1; page <= reader.NumberOfPages; page++)
        {
            pages.Add(reader.GetPageOrigRef(page).Number);
        }

        foreach (var paragraph in Paragraphs(reader))
        {
            var owned = paragraph.Get(PdfName.Pg) as PrIndirectReference;
            Assert.IsNotNull(owned, message: "a structure element lost its page reference");
            Assert.IsTrue(pages.Contains(owned.Number), message: "a structure element points at a dropped page");
        }
    }

    /// <summary>
    ///     /ParentTree is keyed by the /StructParents of each page, so dropping a page has to
    ///     renumber both sides or the two stop agreeing.
    /// </summary>
    [TestMethod]
    public void Verify_SelectPages_RenumbersStructParentsToMatchTheParentTree()
    {
        var reader = SelectFrom(TaggedDocument(), pagesToKeep: new[] { 2, 3 });

        var structTreeRoot = reader.Catalog.GetAsDict(PdfName.Structtreeroot);
        var nums = structTreeRoot.GetAsDict(PdfName.Parenttree).GetAsArray(PdfName.Nums);
        Assert.IsNotNull(nums, message: "/ParentTree has no /Nums");
        Assert.AreEqual(expected: 4, nums.Size, message: "expected one key and one entry per kept page");

        var keys = new List<int>();

        for (var index = 0; index < nums.Size; index += 2)
        {
            keys.Add(nums.GetAsNumber(index).IntValue);
        }

        CollectionAssert.AreEqual(new[] { 0, 1 }, keys, message: "/ParentTree keys were not renumbered");

        for (var page = 1; page <= reader.NumberOfPages; page++)
        {
            var structParents = reader.GetPageN(page).GetAsNumber(PdfName.Structparents);
            Assert.IsNotNull(structParents, message: $"page {page} lost /StructParents");
            Assert.IsTrue(keys.Contains(structParents.IntValue),
                message: $"page {page} points at a /ParentTree key that is not there");
        }

        Assert.AreEqual(expected: 2, structTreeRoot.GetAsNumber(PdfName.Parenttreenextkey).IntValue);
    }

    [TestMethod]
    public void Verify_SelectPages_LeavesAnUntaggedDocumentAlone()
    {
        var reader = SelectFrom(UntaggedDocument(), pagesToKeep: new[] { 1 });

        Assert.AreEqual(expected: 1, reader.NumberOfPages);
        Assert.IsNull(reader.Catalog.Get(PdfName.Structtreeroot), message: "a structure tree appeared from nowhere");
    }

    private static PdfReader SelectFrom(byte[] document, ICollection<int> pagesToKeep)
    {
        var reader = new PdfReader(document);
        reader.SelectPages(pagesToKeep);

        // Through the stamper, so the assertions run on a document that was written out and read
        // back rather than on the reader's own object graph.
        using var output = new MemoryStream();
        var stamper = new PdfStamper(reader, output);
        stamper.Close();
        reader.Close();

        return new PdfReader(output.ToArray());
    }

    /// <summary>Every /P below the root, which is one per page in the fixture.</summary>
    private static List<PdfDictionary> Paragraphs(PdfReader reader)
    {
        var found = new List<PdfDictionary>();
        var structTreeRoot = reader.Catalog.GetAsDict(PdfName.Structtreeroot);

        if (structTreeRoot == null)
        {
            return found;
        }

        Walk(structTreeRoot.Get(PdfName.K), found);

        return found;
    }

    private static void Walk(PdfObject node, List<PdfDictionary> found)
    {
        if (PdfReader.GetPdfObject(node) is PdfArray array)
        {
            foreach (var kid in array.ArrayList)
            {
                Walk(kid, found);
            }

            return;
        }

        if (PdfReader.GetPdfObject(node) is not PdfDictionary element)
        {
            return;
        }

        if (PdfName.P.Equals(element.Get(PdfName.S)))
        {
            found.Add(element);
        }

        if (element.Get(PdfName.K) != null)
        {
            Walk(element.Get(PdfName.K), found);
        }
    }

    /// <summary>
    ///     A tagged document with one paragraph per page, each holding one marked content sequence,
    ///     which is the smallest thing that can show the difference between a pruned tree and one
    ///     left describing pages that are gone.
    /// </summary>
    private static byte[] TaggedDocument()
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

            for (var page = 1; page <= PagesInFixture; page++)
            {
                var paragraph = new PdfStructureElement(root, PdfName.P);
                var content = writer.DirectContent;
                content.BeginMarkedContentSequence(paragraph);
                content.BeginText();
                content.SetFontAndSize(font, size: 12);
                content.SetTextMatrix(x: 50, y: 700);
                content.ShowText($"Page {page}");
                content.EndText();
                content.EndMarkedContentSequence();

                if (page < PagesInFixture)
                {
                    writer.PageEmpty = false;
                    document.NewPage();
                }
            }
        }

        return output.ToArray();
    }

    private static byte[] UntaggedDocument()
    {
        using var output = new MemoryStream();

        using (var document = new Document(PageSize.A4))
        {
            var writer = PdfWriter.GetInstance(document, output);
            writer.CloseStream = false;
            document.Open();

            for (var page = 1; page <= PagesInFixture; page++)
            {
                document.Add(new Paragraph($"Page {page}"));

                if (page < PagesInFixture)
                {
                    document.NewPage();
                }
            }
        }

        return output.ToArray();
    }
}
