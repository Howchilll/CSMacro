namespace HowMacro.Unity.CodeGen
{
    /// <summary>
    /// Written by unity/pack_upm.py: a hash of the bundled bridge. Unity caches post-processed assemblies by their inputs,
    /// and this assembly is one of them, so a new bridge changes this constant and makes Unity expand everything again.
    /// </summary>
    static class BridgeStamp
    {
        public const string Value = "a04b97614cebc3f8";
    }
}
