using System.Collections.Generic;
using System.IO;
using System.Text;
using iTextSharp.text;
using iTextSharp.text.pdf;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace iTextSharp.LGPLv2.Core.FunctionalTests;

/// <summary>
///     The library could write a tagged document from scratch, but not add tagged content to one that
///     arrived tagged: PdfWriter.SetTagged builds a tree of its own, which is no use to a stamper.
///     Text stamped onto a page therefore landed as bare content - visible, selectable, and invisible
///     to a screen reader.
/// </summary>
[TestClass]
public class PdfStructureStampTests
{
    [TestMethod]
    public void Verify_StampedText_GetsAStructureElementOfItsOwn()
    {
        var before = Paragraphs(new PdfReader(TaggedDocuments.Create(pages: 2)));
        var reader = Stamp(TaggedDocuments.Create(pages: 2), page: 1);

        Assert.AreEqual(before + 1, Paragraphs(reader), message: "the stamped text was not given an element");
    }

    [TestMethod]
    public void Verify_StampedText_IsMarkedInTheContentStream()
    {
        var reader = Stamp(TaggedDocuments.Create(pages: 2), page: 1);

        var content = Encoding.ASCII.GetString(reader.GetPageContent(pageNum: 1));
        StringAssert.Contains(content, "/P <</MCID", message: "the stamped text is not inside a marked content sequence");
        StringAssert.Contains(content, "EMC");
    }

    /// <summary>
    ///     The new id has to clear whatever the page already uses, or the element points at content
    ///     that belongs to something else.
    /// </summary>
    [TestMethod]
    public void Verify_StampedText_TakesTheNextFreeMarkedContentId()
    {
        var reader = Stamp(TaggedDocuments.Create(pages: 2), page: 1);

        var content = Encoding.ASCII.GetString(reader.GetPageContent(pageNum: 1));
        StringAssert.Contains(content, "/MCID 0", message: "the page lost the id it already had");
        StringAssert.Contains(content, "/MCID 1", message: "the stamp did not take the next free id");
    }

    /// <summary>/ParentTree has to name the new element, or a reader cannot get from page to tag.</summary>
    [TestMethod]
    public void Verify_StampedText_IsReachableThroughTheParentTree()
    {
        var reader = Stamp(TaggedDocuments.Create(pages: 2), page: 1);

        var structTreeRoot = TaggedDocuments.StructTreeRoot(reader);
        var nums = structTreeRoot.GetAsDict(PdfName.Parenttree).GetAsArray(PdfName.Nums);
        var structParents = reader.GetPageN(pageNum: 1).GetAsNumber(PdfName.Structparents);
        Assert.IsNotNull(structParents, message: "the stamped page has no /StructParents");

        PdfArray entry = null;

        for (var index = 0; index < nums.Size; index += 2)
        {
            if (nums.GetAsNumber(index).IntValue == structParents.IntValue)
            {
                entry = nums.GetAsArray(index + 1);
            }
        }

        Assert.IsNotNull(entry, message: "/ParentTree has no entry for the stamped page");
        Assert.AreEqual(expected: 2, entry.Size, message: "/ParentTree does not name both of the page's tags");
    }

    [TestMethod]
    public void Verify_StampingAnUntaggedDocument_AddsNoStructure()
    {
        var reader = Stamp(TaggedDocuments.Untagged(pages: 2), page: 1);

        Assert.IsNull(TaggedDocuments.StructTreeRoot(reader), message: "a structure tree appeared from nowhere");
        var content = Encoding.ASCII.GetString(reader.GetPageContent(pageNum: 1));
        Assert.IsFalse(content.Contains("BDC"), message: "an untagged document was given marked content");
    }


    /// <summary>
    /// A tagged document hangs its content off one element below the root, and a stamp that becomes a
    /// sibling of that leaves two trees with nothing saying which is read first. It has to go inside.
    /// </summary>
    [TestMethod]
    public void Verify_StampedText_GoesInsideTheDocumentElement()
    {
        var reader = Stamp(TaggedDocuments.Create(pages: 2), page: 1);

        var kids = TaggedDocuments.StructTreeRoot(reader).GetAsArray(PdfName.K);
        Assert.AreEqual(expected: 1, kids?.Size ?? 1, message: "the stamp was added beside the document element");
    }

    /// <summary>
    /// Reading order is the point of tagging an address: stamped at the top of the page and read out
    /// last is not an accessible document, however correct the tree looks.
    /// </summary>
    [TestMethod]
    public void Verify_StampedText_CanBePlacedFirstInReadingOrder()
    {
        var appended = ParagraphOrder(Stamp(TaggedDocuments.Create(pages: 2), page: 1, readFirst: false));
        var prepended = ParagraphOrder(Stamp(TaggedDocuments.Create(pages: 2), page: 1, readFirst: true));

        Assert.AreEqual(expected: 2, appended, message: "an appended stamp should be read after the page's own text");
        Assert.AreEqual(expected: 0, prepended, message: "a stamp asked to read first is not first");
    }

    /// <summary>The position of the stamped paragraph among its siblings, by its marked content id.</summary>
    private static int ParagraphOrder(PdfReader reader)
    {
        var paragraphs = TaggedDocuments.ElementsWithRole(reader, PdfName.P);
        for (var index = 0; index < paragraphs.Count; index++)
        {
            if (paragraphs[index].GetAsNumber(PdfName.K)?.IntValue == 1) return index;
        }

        return -1;
    }

    private static int Paragraphs(PdfReader reader)
    {
        var count = TaggedDocuments.ElementsWithRole(reader, PdfName.P).Count;
        reader.Close();

        return count;
    }

    /// <summary>Stamps one line of tagged text onto the given page, as a caller would.</summary>
    private static PdfReader Stamp(byte[] document, int page) => Stamp(document, page, readFirst: false);

    private static PdfReader Stamp(byte[] document, int page, bool readFirst)
    {
        using var output = new MemoryStream();
        var reader = new PdfReader(document);
        var stamper = new PdfStamper(reader, output);
        var tagged = new PdfStructureStamp(stamper);

        var content = stamper.GetOverContent(page);
        tagged.Begin(content, page, PdfName.P, readFirst);
        content.BeginText();
        content.SetFontAndSize(BaseFont.CreateFont(), size: 10);
        content.SetTextMatrix(x: 50, y: 500);
        content.ShowText("Stamped");
        content.EndText();
        tagged.End(content);

        tagged.Complete();
        stamper.Close();
        reader.Close();

        return new PdfReader(output.ToArray());
    }
}
