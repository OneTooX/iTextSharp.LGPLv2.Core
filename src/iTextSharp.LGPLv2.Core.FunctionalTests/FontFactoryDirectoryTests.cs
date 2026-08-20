using System.IO;
using iTextSharp.text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace iTextSharp.LGPLv2.Core.FunctionalTests;

[TestClass]
public class FontFactoryDirectoryTests
{
    /// <summary>
    ///     scanSubdirectories used to do nothing at all: the loop asked Directory.GetFiles for the
    ///     entries and then tested each one with Directory.Exists, which is never true for a file.
    ///     The Java original it was ported from used File.listFiles, which returns directories too.
    ///     It went unnoticed on Windows, where c:/windows/fonts is flat, and broke everything on
    ///     Linux, where /usr/share/fonts holds nothing but subdirectories.
    /// </summary>
    [TestMethod]
    public void Test_RegisterDirectory_Finds_Fonts_In_Subdirectories()
    {
        var root = Path.Combine(Path.GetTempPath(), "FontFactoryDirectoryTests", Path.GetRandomFileName());
        var nested = Path.Combine(root, "truetype", "vendor");
        Directory.CreateDirectory(nested);
        var font = Path.Combine(nested, "tahoma.ttf");
        File.Copy(TestUtils.GetTahomaFontPath(), font);

        try
        {
            Assert.AreEqual(expected: 0, FontFactory.RegisterDirectory(root),
                message: "the root holds no font files of its own");

            Assert.AreEqual(expected: 1, FontFactory.RegisterDirectory(root, scanSubdirectories: true),
                message: "the font two levels down was not registered");
            Assert.IsTrue(FontFactory.IsRegistered(fontname: "tahoma"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
