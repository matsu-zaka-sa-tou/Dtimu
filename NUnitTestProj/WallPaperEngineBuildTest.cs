using MediaReader;

namespace NUnitTestProj
{
    public class WallPaperEngineBuildTest
    {
        string targetP = "";
        [SetUp]
        public void Setup()
        {
            targetP = "J:\\SteamLibrary\\steamapps\\workshop\\content\\431960";
        }

        [Test]
        public void BuildIndex()
        {
            AppId431960IndexUtil.BuildIndex(targetP);
        }
    }
}
