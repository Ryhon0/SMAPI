using System;

namespace StardewModdingAPI
{
    /// <summary>Basic metadata for a content asset.</summary>
    public interface IAssetInfo
    {
        /*********
        ** Accessors
        *********/
        /// <summary>The content's locale code, if the content is localized.</summary>
        string Locale { get; }

        /// <summary>The asset name being read.</summary>
        public IAssetName Name { get; }

        /// <summary>The normalized asset name being read. The format may change between platforms; see <see cref="AssetNameEquals"/> to compare with a known path.</summary>
        [Obsolete($"Use {nameof(Name)} instead.")]
        string AssetName { get; }

        /// <summary>The content data type.</summary>
        Type DataType { get; }


        /*********
        ** Public methods
        *********/
        /// <summary>Get whether the asset name being loaded matches a given name after normalization.</summary>
        /// <param name="path">The expected asset path, relative to the game's content folder and without the .xnb extension or locale suffix (like 'Data\ObjectInformation').</param>
        [Obsolete($"Use {nameof(Name)}.{nameof(IAssetName.IsEquivalentTo)} instead.")]
        bool AssetNameEquals(string path);
    }
}
