using System.IO;
using System.Text;
using iTextSharp.text.pdf;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace iTextSharp.LGPLv2.Core.FunctionalTests;

/// <summary>
///     An image that has been replaced by a blank one is still drawn and still tagged, so the tree
///     goes on describing content that no longer says anything. Retagging the sequence as an artifact
///     is what takes it out of the reading order without taking it off the page.
/// </summary>
[TestClass]
public class PdfContentArtifactsTests
{
    [TestMethod]
    public void Verify_TheSequenceDrawingTheImage_BecomesAnArtifact()
    {
        var reader = new PdfReader(Retag(TaggedDocuments.TextThenFigure(), out var retagged));

        Assert.AreEqual(expected: 1, retagged, message: "the sequence drawing the image was not found");
        var content = Encoding.ASCII.GetString(reader.GetPageContent(pageNum: 1));
        StringAssert.Contains(content, "/Artifact BMC", message: "the image is not inside an artifact");
        Assert.IsFalse(content.Contains("/Figure <</MCID"), message: "the figure is still tagged");
    }

    /// <summary>
    ///     The point is to take the tag away, not the content: the page has to go on drawing what it
    ///     drew, or the caller has removed something it only meant to stop announcing.
    /// </summary>
    [TestMethod]
    public void Verify_TheImage_IsStillDrawn()
    {
        var reader = new PdfReader(Retag(TaggedDocuments.TextThenFigure(), out _));

        var content = Encoding.ASCII.GetString(reader.GetPageContent(pageNum: 1));
        StringAssert.Contains(content, " Do", message: "the image is no longer drawn");
        Assert.IsNotNull(ImageOn(reader, page: 1), message: "the image is no longer in the page resources");
    }

    [TestMethod]
    public void Verify_TheStructureElement_IsRemoved()
    {
        var reader = new PdfReader(Retag(TaggedDocuments.TextThenFigure(), out _));

        Assert.AreEqual(expected: 0, TaggedDocuments.ElementsWithRole(reader, new PdfName(name: "Figure")).Count,
            message: "the element describing the image is still in the tree");
    }

    /// <summary>Everything the caller did not ask about has to come through untouched.</summary>
    [TestMethod]
    public void Verify_TheOtherContent_KeepsItsElement()
    {
        var reader = new PdfReader(Retag(TaggedDocuments.TextThenFigure(), out _));

        CollectionAssert.AreEqual(new[] { "/P" }, TaggedDocuments.RolesInOrder(reader),
            message: "the paragraph beside the image did not survive");
    }

    /// <summary>
    ///     /ParentTree is the reverse of the tree, so an id that is no longer tagged must not still be
    ///     named there - and the entry is indexed by id, so the one that is left has to keep its place.
    /// </summary>
    [TestMethod]
    public void Verify_ParentTree_NoLongerNamesTheRetaggedId()
    {
        var reader = new PdfReader(Retag(TaggedDocuments.TextThenFigure(), out _));

        var nums = TaggedDocuments.StructTreeRoot(reader).GetAsDict(PdfName.Parenttree).GetAsArray(PdfName.Nums);
        Assert.AreEqual(expected: 2, nums.Size, message: "the one page should have one entry");
        Assert.AreEqual(expected: 1, nums.GetAsArray(idx: 1).Size,
            message: "/ParentTree still names the marked content id that became an artifact");
    }

    /// <summary>An image the caller did not name is somebody else's content and stays tagged.</summary>
    [TestMethod]
    public void Verify_AnImageNotAskedAbout_IsLeftAlone()
    {
        var reader = new PdfReader(TaggedDocuments.TextThenFigure());

        var retagged = PdfContentArtifacts.MarkXObjectsAsArtifacts(reader, new PdfObject[] { });

        Assert.AreEqual(expected: 0, retagged);
        CollectionAssert.AreEqual(new[] { "/P", "/Figure" },
            TaggedDocuments.RolesInOrder(new PdfReader(Write(reader))),
            message: "a document nothing was asked about came back changed");
    }

    /// <summary>Asked twice, the second pass has nothing to do: an artifact is not tagged content.</summary>
    [TestMethod]
    public void Verify_RetaggingTwice_ChangesNothingTheSecondTime()
    {
        var reader = new PdfReader(Retag(TaggedDocuments.TextThenFigure(), out _));

        Assert.AreEqual(expected: 0,
            PdfContentArtifacts.MarkXObjectsAsArtifacts(reader, new[] { ImageOn(reader, page: 1) }),
            message: "an artifact was retagged again");
    }

    /// <summary>An untagged document has no tree to take anything out of, and must not grow one.</summary>
    [TestMethod]
    public void Verify_AnUntaggedDocument_IsLeftAlone()
    {
        var reader = new PdfReader(TaggedDocuments.UntaggedFigure());

        var retagged = PdfContentArtifacts.MarkXObjectsAsArtifacts(reader, new[] { ImageOn(reader, page: 1) });

        Assert.AreEqual(expected: 0, retagged, message: "content that was never tagged was retagged");
        Assert.IsNull(TaggedDocuments.StructTreeRoot(new PdfReader(Write(reader))),
            message: "a structure tree appeared from nowhere");
    }

    private static byte[] Retag(byte[] document, out int retagged)
    {
        var reader = new PdfReader(document);
        retagged = PdfContentArtifacts.MarkXObjectsAsArtifacts(reader, new[] { ImageOn(reader, page: 1) });

        return Write(reader);
    }

    private static byte[] Write(PdfReader reader)
    {
        using var output = new MemoryStream();
        var stamper = new PdfStamper(reader, output);
        stamper.Close();
        reader.Close();

        return output.ToArray();
    }

    /// <summary>The first image in a page's resources, as the reference the page draws it by.</summary>
    private static PdfObject ImageOn(PdfReader reader, int page)
    {
        var resources = PdfReader.GetPdfObject(reader.GetPageN(page).Get(PdfName.Resources)) as PdfDictionary;

        if (PdfReader.GetPdfObject(resources?.Get(PdfName.Xobject)) is not PdfDictionary xobjects)
        {
            return null;
        }

        foreach (var name in xobjects.Keys)
        {
            var reference = xobjects.Get(name);

            if (PdfReader.GetPdfObject(reference) is PdfDictionary xobject
                && PdfName.Image.Equals(xobject.Get(PdfName.Subtype)))
            {
                return reference;
            }
        }

        return null;
    }
}
