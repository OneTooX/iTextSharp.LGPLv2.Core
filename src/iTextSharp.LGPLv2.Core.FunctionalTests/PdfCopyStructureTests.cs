using System.Collections.Generic;
using System.IO;
using iTextSharp.text;
using iTextSharp.text.pdf;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace iTextSharp.LGPLv2.Core.FunctionalTests;

/// <summary>
///     PdfCopy imported page content as it stood, marked content operators included, but had no idea
///     a structure tree existed. Everything assembled with it came out with tags on the page and
///     nothing pointing at them - no /StructTreeRoot, no /MarkInfo, no /Lang - so a document with a
///     cover page in front of it was untagged to a reader while looking tagged in the file.
/// </summary>
[TestClass]
public class PdfCopyStructureTests
{
    [TestMethod]
    public void Verify_Concatenating_TaggedDocuments_KeepsBothTrees()
    {
        var reader = Concatenate(TaggedDocuments.Create(pages: 1, label: "Cover"),
            TaggedDocuments.Create(pages: 3, label: "Letter"));

        Assert.AreEqual(expected: 4, reader.NumberOfPages);
        Assert.IsNotNull(TaggedDocuments.StructTreeRoot(reader), message: "the assembled document is untagged");
        Assert.AreEqual(expected: 4, TaggedDocuments.ElementsWithRole(reader, PdfName.P).Count,
            message: "the assembled tree does not hold one paragraph per page");
    }

    [TestMethod]
    public void Verify_Concatenating_MarksTheDocumentAndKeepsItsLanguage()
    {
        var reader = Concatenate(TaggedDocuments.Create(pages: 1), TaggedDocuments.Create(pages: 2));

        var markInfo = reader.Catalog.GetAsDict(PdfName.Markinfo);
        Assert.IsNotNull(markInfo, message: "/MarkInfo is missing");
        Assert.IsTrue(markInfo.GetAsBoolean(PdfName.Marked).BooleanValue, message: "/MarkInfo /Marked is not set");
    }

    /// <summary>
    ///     The point of the exercise: every tag has to name a page the assembled document actually
    ///     has, and /ParentTree has to agree with what the pages say about themselves.
    /// </summary>
    [TestMethod]
    public void Verify_Concatenating_LeavesNoTagPointingOutsideTheDocument()
    {
        var reader = Concatenate(TaggedDocuments.Create(pages: 2), TaggedDocuments.Create(pages: 2));

        var pages = new HashSet<int>(TaggedDocuments.PageObjectNumbers(reader));
        var referenced = TaggedDocuments.PagesReferenced(reader);
        Assert.AreEqual(expected: 4, referenced.Count, message: "not every paragraph kept its page reference");

        foreach (var page in referenced)
        {
            Assert.IsTrue(pages.Contains(page), message: "a tag points at a page the document does not have");
        }
    }

    [TestMethod]
    public void Verify_Concatenating_KeysTheParentTreeByPositionInTheResult()
    {
        var reader = Concatenate(TaggedDocuments.Create(pages: 1), TaggedDocuments.Create(pages: 3));

        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 }, TaggedDocuments.ParentTreeKeys(reader),
            message: "/ParentTree is not keyed by the position of each page in the assembled document");

        for (var page = 1; page <= reader.NumberOfPages; page++)
        {
            var structParents = reader.GetPageN(page).GetAsNumber(PdfName.Structparents);
            Assert.IsNotNull(structParents, message: $"page {page} has no /StructParents");
            Assert.AreEqual(page - 1, structParents.IntValue,
                message: $"page {page} does not point at its own /ParentTree entry");
        }
    }

    /// <summary>
    ///     Mixing a tagged document with an untagged one has to keep what structure there is without
    ///     inventing any: the untagged pages get no /StructParents, because there is nothing to key.
    /// </summary>
    [TestMethod]
    public void Verify_Concatenating_AnUntaggedDocument_KeepsTheTaggedOnesStructure()
    {
        var reader = Concatenate(TaggedDocuments.Create(pages: 2), TaggedDocuments.Untagged(pages: 2));

        Assert.AreEqual(expected: 4, reader.NumberOfPages);
        Assert.AreEqual(expected: 2, TaggedDocuments.ElementsWithRole(reader, PdfName.P).Count);
        CollectionAssert.AreEqual(new[] { 0, 1 }, TaggedDocuments.ParentTreeKeys(reader));

        Assert.IsNotNull(reader.GetPageN(pageNum: 1).GetAsNumber(PdfName.Structparents));
        Assert.IsNull(reader.GetPageN(pageNum: 3).GetAsNumber(PdfName.Structparents),
            message: "an untagged page was given a /ParentTree key");
    }

    [TestMethod]
    public void Verify_Concatenating_UntaggedDocuments_AddsNoStructure()
    {
        var reader = Concatenate(TaggedDocuments.Untagged(pages: 2), TaggedDocuments.Untagged(pages: 1));

        Assert.AreEqual(expected: 3, reader.NumberOfPages);
        Assert.IsNull(TaggedDocuments.StructTreeRoot(reader), message: "a structure tree appeared from nowhere");
    }

    /// <summary>Mirrors what a caller does: hand PdfCopy several documents and read the result back.</summary>
    private static PdfReader Concatenate(params byte[][] documents)
    {
        using var output = new MemoryStream();

        using (var document = new Document())
        {
            var copy = new PdfCopy(document, output);
            copy.CloseStream = false;
            document.Open();

            foreach (var source in documents)
            {
                var reader = new PdfReader(source);

                for (var page = 1; page <= reader.NumberOfPages; page++)
                {
                    copy.AddPage(copy.GetImportedPage(reader, page));
                }

                copy.FreeReader(reader);
                reader.Close();
            }

            copy.Close();
        }

        return new PdfReader(output.ToArray());
    }
}
