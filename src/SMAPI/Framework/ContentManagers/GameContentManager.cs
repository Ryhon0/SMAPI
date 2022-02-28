using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI.Framework.Content;
using StardewModdingAPI.Framework.Exceptions;
using StardewModdingAPI.Framework.Reflection;
using StardewModdingAPI.Framework.Utilities;
using StardewModdingAPI.Internal;
using StardewValley;
using xTile;
using xTile.Tiles;

namespace StardewModdingAPI.Framework.ContentManagers
{
    /// <summary>A content manager which handles reading files from the game content folder with support for interception.</summary>
    internal class GameContentManager : BaseContentManager
    {
        /*********
        ** Fields
        *********/
        /// <summary>The assets currently being intercepted by <see cref="IAssetLoader"/> instances. This is used to prevent infinite loops when a loader loads a new asset.</summary>
        private readonly ContextHash<string> AssetsBeingLoaded = new ContextHash<string>();

        /// <summary>Interceptors which provide the initial versions of matching assets.</summary>
        private IList<ModLinked<IAssetLoader>> Loaders => this.Coordinator.Loaders;

        /// <summary>Interceptors which edit matching assets after they're loaded.</summary>
        private IList<ModLinked<IAssetEditor>> Editors => this.Coordinator.Editors;

        /// <summary>Maps asset names to their localized form, like <c>LooseSprites\Billboard => LooseSprites\Billboard.fr-FR</c> (localized) or <c>Maps\AnimalShop => Maps\AnimalShop</c> (not localized).</summary>
        private IDictionary<string, string> LocalizedAssetNames => LocalizedContentManager.localizedAssetNames;

        /// <summary>Whether the next load is the first for any game content manager.</summary>
        private static bool IsFirstLoad = true;

        /// <summary>A callback to invoke the first time *any* game content manager loads an asset.</summary>
        private readonly Action OnLoadingFirstAsset;


        /*********
        ** Public methods
        *********/
        /// <summary>Construct an instance.</summary>
        /// <param name="name">A name for the mod manager. Not guaranteed to be unique.</param>
        /// <param name="serviceProvider">The service provider to use to locate services.</param>
        /// <param name="rootDirectory">The root directory to search for content.</param>
        /// <param name="currentCulture">The current culture for which to localize content.</param>
        /// <param name="coordinator">The central coordinator which manages content managers.</param>
        /// <param name="monitor">Encapsulates monitoring and logging.</param>
        /// <param name="reflection">Simplifies access to private code.</param>
        /// <param name="onDisposing">A callback to invoke when the content manager is being disposed.</param>
        /// <param name="onLoadingFirstAsset">A callback to invoke the first time *any* game content manager loads an asset.</param>
        /// <param name="aggressiveMemoryOptimizations">Whether to enable more aggressive memory optimizations.</param>
        public GameContentManager(string name, IServiceProvider serviceProvider, string rootDirectory, CultureInfo currentCulture, ContentCoordinator coordinator, IMonitor monitor, Reflector reflection, Action<BaseContentManager> onDisposing, Action onLoadingFirstAsset, bool aggressiveMemoryOptimizations)
            : base(name, serviceProvider, rootDirectory, currentCulture, coordinator, monitor, reflection, onDisposing, isNamespaced: false, aggressiveMemoryOptimizations: aggressiveMemoryOptimizations)
        {
            this.OnLoadingFirstAsset = onLoadingFirstAsset;
        }

        /// <inheritdoc />
        public override T Load<T>(string assetName, LocalizedContentManager.LanguageCode language, bool useCache)
        {
            // raise first-load callback
            if (GameContentManager.IsFirstLoad)
            {
                GameContentManager.IsFirstLoad = false;
                this.OnLoadingFirstAsset();
            }

            // normalize asset name
            IAssetName parsedName = this.Coordinator.ParseAssetName(assetName);
            if (parsedName.LanguageCode.HasValue)
                return this.Load<T>(parsedName.BaseName, parsedName.LanguageCode.Value, useCache);

            // get from cache
            if (useCache && this.IsLoaded(parsedName.Name, language))
                return this.RawLoad<T>(parsedName.Name, language, useCache: true);

            // get managed asset
            if (this.Coordinator.TryParseManagedAssetKey(parsedName.Name, out string contentManagerID, out string relativePath))
            {
                T managedAsset = this.Coordinator.LoadManagedAsset<T>(contentManagerID, relativePath);
                this.TrackAsset(parsedName.Name, managedAsset, language, useCache);
                return managedAsset;
            }

            // load asset
            T data;
            if (this.AssetsBeingLoaded.Contains(parsedName.Name))
            {
                this.Monitor.Log($"Broke loop while loading asset '{parsedName.Name}'.", LogLevel.Warn);
                this.Monitor.Log($"Bypassing mod loaders for this asset. Stack trace:\n{Environment.StackTrace}");
                data = this.RawLoad<T>(parsedName.Name, language, useCache);
            }
            else
            {
                data = this.AssetsBeingLoaded.Track(parsedName.Name, () =>
                {
                    string locale = this.GetLocale(language);
                    IAssetInfo info = new AssetInfo(locale, parsedName, typeof(T), this.AssertAndNormalizeAssetName);
                    IAssetData asset =
                        this.ApplyLoader<T>(info)
                        ?? new AssetDataForObject(info, this.RawLoad<T>(parsedName.Name, language, useCache), this.AssertAndNormalizeAssetName);
                    asset = this.ApplyEditors<T>(info, asset);
                    return (T)asset.Data;
                });
            }

            // update cache & return data
            this.TrackAsset(parsedName.Name, data, language, useCache);
            return data;
        }

        /// <inheritdoc />
        public override void OnLocaleChanged()
        {
            base.OnLocaleChanged();

            // find assets for which a translatable version was loaded
            HashSet<string> removeAssetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in this.LocalizedAssetNames.Where(p => p.Key != p.Value).Select(p => p.Key))
            {
                IAssetName assetName = this.Coordinator.ParseAssetName(key);
                removeAssetNames.Add(assetName.BaseName);
            }

            // invalidate translatable assets
            List<IAssetName> invalidated = new();
            foreach ((string key, _) in this.GetCachedAssets().ToArray())
            {
                IAssetName assetName = this.Coordinator.ParseAssetName(key);
                if (removeAssetNames.Contains(assetName.Name) || removeAssetNames.Contains(assetName.BaseName))
                {
                    invalidated.Add(assetName);
                    this.Cache.Remove(key, dispose: false);

                    this.InvalidateCache(assetName);
                }
            }
            if (invalidated.Any())
                this.Monitor.Log($"Invalidated {invalidated.Count} asset names: {string.Join(", ", invalidated.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))} for locale change.");
        }

        /// <inheritdoc />
        public override LocalizedContentManager CreateTemporary()
        {
            return this.Coordinator.CreateGameContentManager("(temporary)");
        }


        /*********
        ** Private methods
        *********/
        /// <inheritdoc />
        protected override bool IsNormalizedKeyLoaded(string normalizedAssetName, LanguageCode language)
        {
            string cachedKey = null;
            bool localized =
                language != LocalizedContentManager.LanguageCode.en
                && !this.Coordinator.IsManagedAssetKey(normalizedAssetName)
                && this.LocalizedAssetNames.TryGetValue(normalizedAssetName, out cachedKey);

            return localized
                ? this.Cache.ContainsKey(cachedKey)
                : this.Cache.ContainsKey(normalizedAssetName);
        }

        /// <inheritdoc />
        protected override void TrackAsset<T>(string assetName, T value, LanguageCode language, bool useCache)
        {
            // handle explicit language in asset name
            {
                IAssetName parsedName = this.Coordinator.ParseAssetName(assetName);
                if (parsedName.LanguageCode.HasValue)
                {
                    this.TrackAsset(parsedName.BaseName, value, parsedName.LanguageCode.Value, useCache);
                    return;
                }
            }

            // save to cache
            // Note: even if the asset was loaded and cached right before this method was called,
            // we need to fully re-inject it here for two reasons:
            //   1. So we can look up an asset by its base or localized key (the game/XNA logic
            //      only caches by the most specific key).
            //   2. Because a mod asset loader/editor may have changed the asset in a way that
            //      doesn't change the instance stored in the cache, e.g. using `asset.ReplaceWith`.
            if (useCache)
            {
                string translatedKey = $"{assetName}.{this.GetLocale(language)}";
                base.TrackAsset(assetName, value, language, useCache: true);
                if (this.Cache.ContainsKey(translatedKey))
                    base.TrackAsset(translatedKey, value, language, useCache: true);

                // track whether the injected asset is translatable for is-loaded lookups
                if (this.Cache.ContainsKey(translatedKey))
                    this.LocalizedAssetNames[assetName] = translatedKey;
                else if (this.Cache.ContainsKey(assetName))
                    this.LocalizedAssetNames[assetName] = assetName;
                else
                    this.Monitor.Log($"Asset '{assetName}' could not be found in the cache immediately after injection.", LogLevel.Error);
            }
        }

        /// <summary>Load an asset file directly from the underlying content manager.</summary>
        /// <typeparam name="T">The type of asset to load.</typeparam>
        /// <param name="assetName">The normalized asset key.</param>
        /// <param name="language">The language code for which to load content.</param>
        /// <param name="useCache">Whether to read/write the loaded asset to the asset cache.</param>
        /// <remarks>Derived from <see cref="LocalizedContentManager.Load{T}(string, LocalizedContentManager.LanguageCode)"/>.</remarks>
        private T RawLoad<T>(string assetName, LanguageCode language, bool useCache)
        {
            try
            {
                // use cached key
                if (language == this.Language && this.LocalizedAssetNames.TryGetValue(assetName, out string cachedKey))
                    return base.RawLoad<T>(cachedKey, useCache);

                // try translated key
                if (language != LocalizedContentManager.LanguageCode.en)
                {
                    string translatedKey = $"{assetName}.{this.GetLocale(language)}";
                    try
                    {
                        T obj = base.RawLoad<T>(translatedKey, useCache);
                        this.LocalizedAssetNames[assetName] = translatedKey;
                        return obj;
                    }
                    catch (ContentLoadException)
                    {
                        this.LocalizedAssetNames[assetName] = assetName;
                    }
                }

                // try base asset
                return base.RawLoad<T>(assetName, useCache);
            }
            catch (ContentLoadException ex) when (ex.InnerException is FileNotFoundException innerEx && innerEx.InnerException == null)
            {
                throw new SContentLoadException($"Error loading \"{assetName}\": it isn't in the Content folder and no mod provided it.");
            }
        }

        /// <summary>Load the initial asset from the registered <see cref="Loaders"/>.</summary>
        /// <param name="info">The basic asset metadata.</param>
        /// <returns>Returns the loaded asset metadata, or <c>null</c> if no loader matched.</returns>
        private IAssetData ApplyLoader<T>(IAssetInfo info)
        {
            // find matching loaders
            var loaders = this.Loaders
                .Where(entry =>
                {
                    try
                    {
                        return entry.Data.CanLoad<T>(info);
                    }
                    catch (Exception ex)
                    {
                        entry.Mod.LogAsMod($"Mod failed when checking whether it could load asset '{info.Name}', and will be ignored. Error details:\n{ex.GetLogSummary()}", LogLevel.Error);
                        return false;
                    }
                })
                .ToArray();

            // validate loaders
            if (!loaders.Any())
                return null;
            if (loaders.Length > 1)
            {
                string[] loaderNames = loaders.Select(p => p.Mod.DisplayName).ToArray();
                this.Monitor.Log($"Multiple mods want to provide the '{info.Name}' asset ({string.Join(", ", loaderNames)}), but an asset can't be loaded multiple times. SMAPI will use the default asset instead; uninstall one of the mods to fix this. (Message for modders: you should usually use {typeof(IAssetEditor)} instead to avoid conflicts.)", LogLevel.Warn);
                return null;
            }

            // fetch asset from loader
            IModMetadata mod = loaders[0].Mod;
            IAssetLoader loader = loaders[0].Data;
            T data;
            try
            {
                data = loader.Load<T>(info);
                this.Monitor.Log($"{mod.DisplayName} loaded asset '{info.Name}'.", LogLevel.Trace);
            }
            catch (Exception ex)
            {
                mod.LogAsMod($"Mod crashed when loading asset '{info.Name}'. SMAPI will use the default asset instead. Error details:\n{ex.GetLogSummary()}", LogLevel.Error);
                return null;
            }

            // return matched asset
            return this.TryFixAndValidateLoadedAsset(info, data, mod)
                ? new AssetDataForObject(info, data, this.AssertAndNormalizeAssetName)
                : null;
        }

        /// <summary>Apply any <see cref="Editors"/> to a loaded asset.</summary>
        /// <typeparam name="T">The asset type.</typeparam>
        /// <param name="info">The basic asset metadata.</param>
        /// <param name="asset">The loaded asset.</param>
        private IAssetData ApplyEditors<T>(IAssetInfo info, IAssetData asset)
        {
            IAssetData GetNewData(object data) => new AssetDataForObject(info, data, this.AssertAndNormalizeAssetName);

            // special case: if the asset was loaded with a more general type like 'object', call editors with the actual type instead.
            {
                Type actualType = asset.Data.GetType();
                Type actualOpenType = actualType.IsGenericType ? actualType.GetGenericTypeDefinition() : null;

                if (typeof(T) != actualType && (actualOpenType == typeof(Dictionary<,>) || actualOpenType == typeof(List<>) || actualType == typeof(Texture2D) || actualType == typeof(Map)))
                {
                    return (IAssetData)this.GetType()
                        .GetMethod(nameof(this.ApplyEditors), BindingFlags.NonPublic | BindingFlags.Instance)
                        .MakeGenericMethod(actualType)
                        .Invoke(this, new object[] { info, asset });
                }
            }

            // edit asset
            foreach (var entry in this.Editors)
            {
                // check for match
                IModMetadata mod = entry.Mod;
                IAssetEditor editor = entry.Data;
                try
                {
                    if (!editor.CanEdit<T>(info))
                        continue;
                }
                catch (Exception ex)
                {
                    mod.LogAsMod($"Mod crashed when checking whether it could edit asset '{info.Name}', and will be ignored. Error details:\n{ex.GetLogSummary()}", LogLevel.Error);
                    continue;
                }

                // try edit
                object prevAsset = asset.Data;
                try
                {
                    editor.Edit<T>(asset);
                    this.Monitor.Log($"{mod.DisplayName} edited {info.Name}.");
                }
                catch (Exception ex)
                {
                    mod.LogAsMod($"Mod crashed when editing asset '{info.Name}', which may cause errors in-game. Error details:\n{ex.GetLogSummary()}", LogLevel.Error);
                }

                // validate edit
                if (asset.Data == null)
                {
                    mod.LogAsMod($"Mod incorrectly set asset '{info.Name}' to a null value; ignoring override.", LogLevel.Warn);
                    asset = GetNewData(prevAsset);
                }
                else if (!(asset.Data is T))
                {
                    mod.LogAsMod($"Mod incorrectly set asset '{asset.Name}' to incompatible type '{asset.Data.GetType()}', expected '{typeof(T)}'; ignoring override.", LogLevel.Warn);
                    asset = GetNewData(prevAsset);
                }
            }

            // return result
            return asset;
        }

        /// <summary>Validate that an asset loaded by a mod is valid and won't cause issues, and fix issues if possible.</summary>
        /// <typeparam name="T">The asset type.</typeparam>
        /// <param name="info">The basic asset metadata.</param>
        /// <param name="data">The loaded asset data.</param>
        /// <param name="mod">The mod which loaded the asset.</param>
        /// <returns>Returns whether the asset passed validation checks (after any fixes were applied).</returns>
        private bool TryFixAndValidateLoadedAsset<T>(IAssetInfo info, T data, IModMetadata mod)
        {
            // can't load a null asset
            if (data == null)
            {
                mod.LogAsMod($"SMAPI blocked asset replacement for '{info.Name}': mod incorrectly set asset to a null value.", LogLevel.Error);
                return false;
            }

            // when replacing a map, the vanilla tilesheets must have the same order and IDs
            if (data is Map loadedMap)
            {
                TilesheetReference[] vanillaTilesheetRefs = this.Coordinator.GetVanillaTilesheetIds(info.Name.Name);
                foreach (TilesheetReference vanillaSheet in vanillaTilesheetRefs)
                {
                    // add missing tilesheet
                    if (loadedMap.GetTileSheet(vanillaSheet.Id) == null)
                    {
                        mod.Monitor.LogOnce("SMAPI fixed maps loaded by this mod to prevent errors. See the log file for details.", LogLevel.Warn);
                        this.Monitor.Log($"Fixed broken map replacement: {mod.DisplayName} loaded '{info.Name}' without a required tilesheet (id: {vanillaSheet.Id}, source: {vanillaSheet.ImageSource}).");

                        loadedMap.AddTileSheet(new TileSheet(vanillaSheet.Id, loadedMap, vanillaSheet.ImageSource, vanillaSheet.SheetSize, vanillaSheet.TileSize));
                    }

                    // handle mismatch
                    if (loadedMap.TileSheets.Count <= vanillaSheet.Index || loadedMap.TileSheets[vanillaSheet.Index].Id != vanillaSheet.Id)
                    {
                        // only show warning if not farm map
                        // This is temporary: mods shouldn't do this for any vanilla map, but these are the ones we know will crash. Showing a warning for others instead gives modders time to update their mods, while still simplifying troubleshooting.
                        bool isFarmMap = info.Name.IsEquivalentTo("Maps/Farm") || info.Name.IsEquivalentTo("Maps/Farm_Combat") || info.Name.IsEquivalentTo("Maps/Farm_Fishing") || info.Name.IsEquivalentTo("Maps/Farm_Foraging") || info.Name.IsEquivalentTo("Maps/Farm_FourCorners") || info.Name.IsEquivalentTo("Maps/Farm_Island") || info.Name.IsEquivalentTo("Maps/Farm_Mining");

                        string reason = $"mod reordered the original tilesheets, which {(isFarmMap ? "would cause a crash" : "often causes crashes")}.\nTechnical details for mod author: Expected order: {string.Join(", ", vanillaTilesheetRefs.Select(p => p.Id))}. See https://stardewvalleywiki.com/Modding:Maps#Tilesheet_order for help.";

                        SCore.DeprecationManager.PlaceholderWarn("3.8.2", DeprecationLevel.PendingRemoval);
                        if (isFarmMap)
                        {
                            mod.LogAsMod($"SMAPI blocked '{info.Name}' map load: {reason}", LogLevel.Error);
                            return false;
                        }
                        mod.LogAsMod($"SMAPI found an issue with '{info.Name}' map load: {reason}", LogLevel.Warn);
                    }
                }
            }

            return true;
        }
    }
}
