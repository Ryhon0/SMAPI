using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI.Framework.ContentManagers;
using StardewModdingAPI.Framework.Reflection;
using StardewModdingAPI.Internal;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.GameData;
using StardewValley.GameData.Movies;
using StardewValley.Locations;
using StardewValley.TerrainFeatures;
using xTile;

namespace StardewModdingAPI.Metadata
{
    /// <summary>Propagates changes to core assets to the game state.</summary>
    internal class CoreAssetPropagator
    {
        /*********
        ** Fields
        *********/
        /// <summary>The main content manager through which to reload assets.</summary>
        private readonly LocalizedContentManager MainContentManager;

        /// <summary>An internal content manager used only for asset propagation. See remarks on <see cref="GameContentManagerForAssetPropagation"/>.</summary>
        private readonly GameContentManagerForAssetPropagation DisposableContentManager;

        /// <summary>Writes messages to the console.</summary>
        private readonly IMonitor Monitor;

        /// <summary>Simplifies access to private game code.</summary>
        private readonly Reflector Reflection;

        /// <summary>Whether to enable more aggressive memory optimizations.</summary>
        private readonly bool AggressiveMemoryOptimizations;


        /*********
        ** Public methods
        *********/
        /// <summary>Initialize the core asset data.</summary>
        /// <param name="mainContent">The main content manager through which to reload assets.</param>
        /// <param name="disposableContent">An internal content manager used only for asset propagation.</param>
        /// <param name="monitor">Writes messages to the console.</param>
        /// <param name="reflection">Simplifies access to private code.</param>
        /// <param name="aggressiveMemoryOptimizations">Whether to enable more aggressive memory optimizations.</param>
        public CoreAssetPropagator(LocalizedContentManager mainContent, GameContentManagerForAssetPropagation disposableContent, IMonitor monitor, Reflector reflection, bool aggressiveMemoryOptimizations)
        {
            this.MainContentManager = mainContent;
            this.DisposableContentManager = disposableContent;
            this.Monitor = monitor;
            this.Reflection = reflection;
            this.AggressiveMemoryOptimizations = aggressiveMemoryOptimizations;
        }

        /// <summary>Reload one of the game's core assets (if applicable).</summary>
        /// <param name="contentManagers">The content managers whose assets to update.</param>
        /// <param name="assets">The asset keys and types to reload.</param>
        /// <param name="ignoreWorld">Whether the in-game world is fully unloaded (e.g. on the title screen), so there's no need to propagate changes into the world.</param>
        /// <param name="propagatedAssets">A lookup of asset names to whether they've been propagated.</param>
        /// <param name="updatedNpcWarps">Whether the NPC pathfinding cache was reloaded.</param>
        public void Propagate(IList<IContentManager> contentManagers, IDictionary<IAssetName, Type> assets, bool ignoreWorld, out IDictionary<IAssetName, bool> propagatedAssets, out bool updatedNpcWarps)
        {
            propagatedAssets = assets.ToDictionary(p => p.Key, _ => false);

            // edit textures in-place
            {
                IAssetName[] textureAssets = assets
                    .Where(p => typeof(Texture2D).IsAssignableFrom(p.Value))
                    .Select(p => p.Key)
                    .ToArray();

                if (textureAssets.Any())
                {
                    var defaultLanguage = this.MainContentManager.GetCurrentLanguage();

                    foreach (IAssetName assetName in textureAssets)
                    {
                        bool changed = this.PropagateTexture(assetName, assetName.LanguageCode ?? defaultLanguage, contentManagers, ignoreWorld);
                        if (changed)
                            propagatedAssets[assetName] = true;
                    }

                    foreach (IAssetName assetName in textureAssets)
                        assets.Remove(assetName);
                }
            }

            // reload other assets
            updatedNpcWarps = false;
            foreach (var entry in assets)
            {
                bool changed = false;
                bool curChangedMapWarps = false;
                try
                {
                    changed = this.PropagateOther(entry.Key, entry.Value, ignoreWorld, out curChangedMapWarps);
                }
                catch (Exception ex)
                {
                    this.Monitor.Log($"An error occurred while propagating asset changes. Error details:\n{ex.GetLogSummary()}", LogLevel.Error);
                }

                propagatedAssets[entry.Key] = changed;
                updatedNpcWarps = updatedNpcWarps || curChangedMapWarps;
            }

            // reload NPC pathfinding cache if any map changed
            if (updatedNpcWarps)
                NPC.populateRoutesFromLocationToLocationList();
        }


        /*********
        ** Private methods
        *********/
        /// <summary>Propagate changes to a cached texture asset.</summary>
        /// <param name="assetName">The asset name to reload.</param>
        /// <param name="language">The language for which to get assets.</param>
        /// <param name="contentManagers">The content managers whose assets to update.</param>
        /// <param name="ignoreWorld">Whether the in-game world is fully unloaded (e.g. on the title screen), so there's no need to propagate changes into the world.</param>
        /// <returns>Returns whether an asset was loaded.</returns>
        [SuppressMessage("ReSharper", "StringLiteralTypo", Justification = "These deliberately match the asset names.")]
        private bool PropagateTexture(IAssetName assetName, LocalizedContentManager.LanguageCode language, IList<IContentManager> contentManagers, bool ignoreWorld)
        {
            /****
            ** Update textures in-place
            ****/
            Lazy<Texture2D> newTexture = new(() => this.DisposableContentManager.Load<Texture2D>(assetName.BaseName, language, useCache: false));
            bool changed = false;
            foreach (IContentManager contentManager in contentManagers)
            {
                if (contentManager.IsLoaded(assetName.BaseName, language))
                {
                    changed = true;
                    Texture2D texture = contentManager.Load<Texture2D>(assetName.BaseName, language, useCache: true);
                    texture.CopyFromTexture(newTexture.Value);
                }
            }

            /****
            ** Update game state if needed
            ****/
            if (changed)
            {
                switch (assetName.Name.ToLower().Replace("\\", "/")) // normalized key so we can compare statically
                {
                    /****
                    ** Buildings
                    ****/
                    case "buildings/houses_paintmask": // Farm
                        if (ignoreWorld)
                        {
                            this.RemoveFromPaintMaskCache(assetName);
                            Game1.getFarm()?.ApplyHousePaint();
                        }
                        break;

                    /****
                    ** Content\Characters\Farmer
                    ****/
                    case "characters/farmer/farmer_base": // Farmer
                    case "characters/farmer/farmer_base_bald":
                    case "characters/farmer/farmer_girl_base":
                    case "characters/farmer/farmer_girl_base_bald":
                        if (ignoreWorld)
                            this.ReloadPlayerSprites(assetName);
                        break;

                    /****
                    ** Content\TileSheets
                    ****/
                    case "tilesheets/tools": // Game1.ResetToolSpriteSheet
                        Game1.ResetToolSpriteSheet();
                        break;

                    default:
                        if (!ignoreWorld)
                        {
                            if (assetName.IsDirectlyUnderPath("Buildings") && assetName.BaseName.EndsWith("_PaintMask"))
                                return this.ReloadBuildingPaintMask(assetName);
                        }

                        break;
                }
            }

            return changed;
        }

        /// <summary>Reload one of the game's core assets (if applicable).</summary>
        /// <param name="assetName">The asset name to reload.</param>
        /// <param name="type">The asset type to reload.</param>
        /// <param name="ignoreWorld">Whether the in-game world is fully unloaded (e.g. on the title screen), so there's no need to propagate changes into the world.</param>
        /// <param name="changedWarps">Whether any map warps were changed as part of this propagation.</param>
        /// <returns>Returns whether an asset was loaded.</returns>
        [SuppressMessage("ReSharper", "StringLiteralTypo", Justification = "These deliberately match the asset names.")]
        private bool PropagateOther(IAssetName assetName, Type type, bool ignoreWorld, out bool changedWarps)
        {
            bool anyChanged = false;
            var content = this.MainContentManager;
            string key = assetName.Name;
            changedWarps = false;

            /****
            ** Propagate map changes
            ****/
            if (type == typeof(Map))
            {
                if (!ignoreWorld)
                {
                    foreach (LocationInfo info in this.GetLocationsWithInfo())
                    {
                        GameLocation location = info.Location;

                        if (assetName.IsEquivalentTo(location.mapPath.Value))
                        {
                            static ISet<string> GetWarpSet(GameLocation location)
                            {
                                return new HashSet<string>(
                                    location.warps.Select(p => $"{p.X} {p.Y} {p.TargetName} {p.TargetX} {p.TargetY}")
                                );
                            }

                            var oldWarps = GetWarpSet(location);
                            this.ReloadMap(info);
                            var newWarps = GetWarpSet(location);

                            changedWarps = changedWarps || oldWarps.Count != newWarps.Count || oldWarps.Any(p => !newWarps.Contains(p));
                            anyChanged = true;
                        }
                    }
                }

                return anyChanged;
            }

            /****
            ** Propagate by key
            ****/
            switch (assetName.Name.ToLower().Replace("\\", "/")) // normalized key so we can compare statically
            {
                /****
                ** Content\Data
                ****/
                case "data/achievements": // Game1.LoadContent
                    Game1.achievements = content.Load<Dictionary<int, string>>(key);
                    return true;

                case "data/audiocuemodificationdata":
                    Game1.CueModification.OnStartup(); // reload file and reapply changes
                    return true;

                case "data/boots": // BootsDataDefinition
                    Utility.ClearParsedItemIDs();
                    return true;

                case "data/bigcraftablesinformation": // Game1.LoadContent
                    Game1.bigCraftablesInformation = content.Load<Dictionary<string, string>>(key);
                    Utility.ClearParsedItemIDs();
                    return true;

                case "data/buildingsdata": // Game1.LoadContent
                    Game1.buildingsData = content.Load<Dictionary<string, BuildingData>>(key);
                    return true;

                case "data/clothinginformation": // Game1.LoadContent
                    Game1.clothingInformation = content.Load<Dictionary<string, string>>(key);
                    Utility.ClearParsedItemIDs();
                    return true;

                case "data/concessions": // MovieTheater.GetConcessions
                    MovieTheater.ClearCachedLocalizedData();
                    return true;

                case "data/concessiontastes": // MovieTheater.GetConcessionTasteForCharacter
                    this.Reflection
                        .GetField<List<ConcessionTaste>>(typeof(MovieTheater), "_concessionTastes")
                        .SetValue(content.Load<List<ConcessionTaste>>(key));
                    return true;

                case "data/cookingrecipes": // CraftingRecipe.InitShared
                    CraftingRecipe.cookingRecipes = content.Load<Dictionary<string, string>>(key);
                    return true;

                case "data/craftingrecipes": // CraftingRecipe.InitShared
                    CraftingRecipe.craftingRecipes = content.Load<Dictionary<string, string>>(key);
                    return true;

                case "data/farmanimals": // FarmAnimal constructor
                    return !ignoreWorld && this.ReloadFarmAnimalData();

                case "data/furniture": // FurnitureDataDefinition
                    Utility.ClearParsedItemIDs();
                    return true;

                case "data/hairdata": // Farmer.GetHairStyleMetadataFile
                    return this.ReloadHairData();

                case "data/hats": // HatDataDefinition
                    Utility.ClearParsedItemIDs();
                    return true;

                case "data/locationcontexts": // GameLocation.LocationContext
                    this.ReloadLocationContexts();
                    return true;

                case "data/movies": // MovieTheater.GetMovieData
                case "data/moviesreactions": // MovieTheater.GetMovieReactions
                    MovieTheater.ClearCachedLocalizedData();
                    return true;

                case "data/npcdispositions": // NPC constructor
                    return !ignoreWorld && this.ReloadNpcDispositions(content, assetName);

                case "data/npcgifttastes": // Game1.LoadContent
                    Game1.NPCGiftTastes = content.Load<Dictionary<string, string>>(key);
                    return true;

                case "data/objectcontexttags": // Game1.LoadContent
                    Game1.objectContextTags = content.Load<Dictionary<string, string>>(key);
                    return true;

                case "data/objectinformation": // Game1.LoadContent
                    Game1.objectInformation = content.Load<Dictionary<string, string>>(key);
                    Utility.ClearParsedItemIDs();
                    return true;

                case "data/tooldata": // Game1.LoadContent
                    Game1.toolData = content.Load<Dictionary<string, string>>(key);
                    Utility.ClearParsedItemIDs();
                    return true;

                case "data/weapons": // WeaponDataDefinition
                    Utility.ClearParsedItemIDs();
                    return true;

                case "data/wildtrees": // Tree
                    this.Reflection.GetField<Dictionary<string, WildTreeTapData>>(typeof(Tree), "_WildTreeData").SetValue(null);
                    this.Reflection.GetField<Dictionary<string, string>>(typeof(Tree), "_WildTreeSeedLookup").SetValue(null);
                    return true;

                /****
                ** Content\Fonts
                ****/
                case "fonts/spritefont1": // Game1.LoadContent
                    Game1.dialogueFont = content.Load<SpriteFont>(key);
                    return true;

                case "fonts/smallfont": // Game1.LoadContent
                    Game1.smallFont = content.Load<SpriteFont>(key);
                    return true;

                case "fonts/tinyfont": // Game1.LoadContent
                    Game1.tinyFont = content.Load<SpriteFont>(key);
                    return true;

                case "fonts/tinyfontborder": // Game1.LoadContent
                    Game1.tinyFontBorder = content.Load<SpriteFont>(key);
                    return true;

                /****
                ** Content\Strings
                ****/
                case "strings/stringsfromcsfiles":
                    return this.ReloadStringsFromCsFiles(content);

                /****
                ** Dynamic keys
                ****/
                default:
                    if (!ignoreWorld)
                    {
                        if (assetName.IsDirectlyUnderPath("Characters/Dialogue"))
                            return this.ReloadNpcDialogue(assetName);

                        if (assetName.IsDirectlyUnderPath("Characters/schedules"))
                            return this.ReloadNpcSchedules(assetName);
                    }

                    return false;
            }
        }


        /*********
        ** Private methods
        *********/
        /****
        ** Reload texture methods
        ****/
        /// <summary>Reload building textures.</summary>
        /// <param name="assetName">The asset name to reload.</param>
        /// <returns>Returns whether any textures were reloaded.</returns>
        private bool ReloadBuildingPaintMask(IAssetName assetName)
        {
            // get building type
            string type = Path.GetFileName(assetName.Name)!;
            type = type.Substring(0, type.Length - "_PaintMask".Length);

            // get buildings
            Building[] buildings = this.GetLocations(buildingInteriors: false)
                .SelectMany(p => p.buildings)
                .Where(p => p.buildingType.Value == type)
                .ToArray();

            // remove from paint mask cache
            bool removedFromCache = this.RemoveFromPaintMaskCache(assetName);

            // reload textures
            if (buildings.Any())
            {
                foreach (Building building in buildings)
                    building.resetTexture();

                return true;
            }

            return removedFromCache;
        }

        /// <summary>Reload the data for matching farm animals.</summary>
        /// <returns>Returns whether any farm animals were affected.</returns>
        /// <remarks>Derived from the <see cref="FarmAnimal"/> constructor.</remarks>
        private bool ReloadFarmAnimalData()
        {
            bool changed = false;
            foreach (FarmAnimal animal in this.GetFarmAnimals())
            {
                animal.reloadData();
                changed = true;
            }

            return changed;
        }

        /// <summary>Reload hair style metadata.</summary>
        /// <returns>Returns whether any assets were reloaded.</returns>
        /// <remarks>Derived from the <see cref="Farmer.GetHairStyleMetadataFile"/> and <see cref="Farmer.GetHairStyleMetadata"/>.</remarks>
        private bool ReloadHairData()
        {
            if (Farmer.hairStyleMetadataFile == null)
                return false;

            Farmer.hairStyleMetadataFile = null;
            Farmer.allHairStyleIndices = null;
            Farmer.hairStyleMetadata.Clear();

            return true;
        }

        /// <summary>Reload location context data.</summary>
        private void ReloadLocationContexts()
        {
            //
            // cached contexts will be reloaded on demand if null
            //

            foreach (string fieldName in new[] { "_defaultContext", "_islandContext" })
                this.Reflection.GetField<LocationContextData>(typeof(GameLocation), fieldName).SetValue(null);

            foreach (GameLocation location in this.GetLocations())
                location.locationContext = null;
        }

        /// <summary>Reload the map for a location.</summary>
        /// <param name="locationInfo">The location whose map to reload.</param>
        private void ReloadMap(LocationInfo locationInfo)
        {
            GameLocation location = locationInfo.Location;
            Vector2? playerPos = Game1.player?.Position;

            if (this.AggressiveMemoryOptimizations)
                location.map.DisposeTileSheets(Game1.mapDisplayDevice);

            // reload map
            location.interiorDoors.Clear(); // prevent errors when doors try to update tiles which no longer exist
            location.reloadMap();

            // reload interior doors
            location.interiorDoors.Clear();
            location.interiorDoors.ResetSharedState(); // load doors from map properties
            location.interiorDoors.ResetLocalState(); // reapply door tiles

            // reapply map changes (after reloading doors so they apply theirs too)
            location.MakeMapModifications(force: true);

            // update for changes
            location.updateWarps();
            location.updateDoors();
            locationInfo.ParentBuilding?.updateInteriorWarps();

            // reset player position
            // The game may move the player as part of the map changes, even if they're not in that
            // location. That's not needed in this case, and it can have weird effects like players
            // warping onto the wrong tile (or even off-screen) if a patch changes the farmhouse
            // map on location change.
            if (playerPos.HasValue)
                Game1.player.Position = playerPos.Value;
        }

        /// <summary>Reload the disposition data for matching NPCs.</summary>
        /// <param name="content">The content manager through which to reload the asset.</param>
        /// <param name="assetName">The asset name to reload.</param>
        /// <returns>Returns whether any NPCs were affected.</returns>
        private bool ReloadNpcDispositions(LocalizedContentManager content, IAssetName assetName)
        {
            IDictionary<string, string> data = content.Load<Dictionary<string, string>>(assetName.Name);
            bool changed = false;
            foreach (NPC npc in this.GetCharacters())
            {
                if (npc.isVillager() && data.ContainsKey(npc.Name))
                {
                    npc.reloadData();
                    changed = true;
                }
            }

            return changed;
        }

        /// <summary>Reload the sprites for matching players.</summary>
        /// <param name="assetName">The asset name to reload.</param>
        private void ReloadPlayerSprites(IAssetName assetName)
        {
            Farmer[] players =
                (
                    from player in Game1.getOnlineFarmers()
                    where assetName.IsEquivalentTo(player.getTexture())
                    select player
                )
                .ToArray();

            foreach (Farmer player in players)
            {
                this.Reflection.GetField<Dictionary<string, Dictionary<int, List<int>>>>(typeof(FarmerRenderer), "_recolorOffsets").GetValue().Remove(player.getTexture());
                player.FarmerRenderer.MarkSpriteDirty();
            }
        }

        /****
        ** Reload data methods
        ****/
        /// <summary>Reload the dialogue data for matching NPCs.</summary>
        /// <param name="assetName">The asset name to reload.</param>
        /// <returns>Returns whether any assets were reloaded.</returns>
        private bool ReloadNpcDialogue(IAssetName assetName)
        {
            // get NPCs
            string name = Path.GetFileName(assetName.Name);
            NPC[] villagers = this.GetCharacters().Where(npc => npc.Name == name && npc.isVillager()).ToArray();
            if (!villagers.Any())
                return false;

            // update dialogue
            // Note that marriage dialogue isn't reloaded after reset, but it doesn't need to be
            // propagated anyway since marriage dialogue keys can't be added/removed and the field
            // doesn't store the text itself.
            foreach (NPC villager in villagers)
            {
                bool shouldSayMarriageDialogue = villager.shouldSayMarriageDialogue.Value;
                MarriageDialogueReference[] marriageDialogue = villager.currentMarriageDialogue.ToArray();

                villager.resetSeasonalDialogue(); // doesn't only affect seasonal dialogue
                villager.resetCurrentDialogue();

                villager.shouldSayMarriageDialogue.Set(shouldSayMarriageDialogue);
                villager.currentMarriageDialogue.Set(marriageDialogue);
            }

            return true;
        }

        /// <summary>Reload the schedules for matching NPCs.</summary>
        /// <param name="assetName">The asset name to reload.</param>
        /// <returns>Returns whether any assets were reloaded.</returns>
        private bool ReloadNpcSchedules(IAssetName assetName)
        {
            // get NPCs
            string name = Path.GetFileName(assetName.Name);
            NPC[] villagers = this.GetCharacters().Where(npc => npc.Name == name && npc.isVillager()).ToArray();
            if (!villagers.Any())
                return false;

            // update schedule
            foreach (NPC villager in villagers)
            {
                // reload schedule
                this.Reflection.GetField<bool>(villager, "_hasLoadedMasterScheduleData").SetValue(false);
                this.Reflection.GetField<Dictionary<string, string>>(villager, "_masterScheduleData").SetValue(null);
                villager.Schedule = villager.getSchedule(Game1.dayOfMonth);

                // switch to new schedule if needed
                if (villager.Schedule != null)
                {
                    int lastScheduleTime = villager.Schedule.Keys.Where(p => p <= Game1.timeOfDay).OrderByDescending(p => p).FirstOrDefault();
                    if (lastScheduleTime != 0)
                    {
                        villager.queuedSchedulePaths.Clear();
                        villager.lastAttemptedSchedule = 0;
                        villager.checkSchedule(lastScheduleTime);
                    }
                }
            }
            return true;
        }

        /// <summary>Reload cached translations from the <c>Strings\StringsFromCSFiles</c> asset.</summary>
        /// <param name="content">The content manager through which to reload the asset.</param>
        /// <returns>Returns whether any data was reloaded.</returns>
        /// <remarks>Derived from the <see cref="Game1.TranslateFields"/>.</remarks>
        private bool ReloadStringsFromCsFiles(LocalizedContentManager content)
        {
            Game1.samBandName = content.LoadString("Strings/StringsFromCSFiles:Game1.cs.2156");
            Game1.elliottBookName = content.LoadString("Strings/StringsFromCSFiles:Game1.cs.2157");

            string[] dayNames = this.Reflection.GetField<string[]>(typeof(Game1), "_shortDayDisplayName").GetValue();
            dayNames[0] = content.LoadString("Strings/StringsFromCSFiles:Game1.cs.3042");
            dayNames[1] = content.LoadString("Strings/StringsFromCSFiles:Game1.cs.3043");
            dayNames[2] = content.LoadString("Strings/StringsFromCSFiles:Game1.cs.3044");
            dayNames[3] = content.LoadString("Strings/StringsFromCSFiles:Game1.cs.3045");
            dayNames[4] = content.LoadString("Strings/StringsFromCSFiles:Game1.cs.3046");
            dayNames[5] = content.LoadString("Strings/StringsFromCSFiles:Game1.cs.3047");
            dayNames[6] = content.LoadString("Strings/StringsFromCSFiles:Game1.cs.3048");

            return true;
        }

        /****
        ** Helpers
        ****/
        /// <summary>Get all NPCs in the game (excluding farm animals).</summary>
        private IEnumerable<NPC> GetCharacters()
        {
            foreach (NPC character in this.GetLocations().SelectMany(p => p.characters))
                yield return character;

            if (Game1.CurrentEvent?.actors != null)
            {
                foreach (NPC character in Game1.CurrentEvent.actors)
                    yield return character;
            }
        }

        /// <summary>Get all farm animals in the game.</summary>
        private IEnumerable<FarmAnimal> GetFarmAnimals()
        {
            foreach (GameLocation location in this.GetLocations())
            {
                if (location is Farm farm)
                {
                    foreach (FarmAnimal animal in farm.animals.Values)
                        yield return animal;
                }
                else if (location is AnimalHouse animalHouse)
                    foreach (FarmAnimal animal in animalHouse.animals.Values)
                        yield return animal;
            }
        }

        /// <summary>Get all locations in the game.</summary>
        /// <param name="buildingInteriors">Whether to also get the interior locations for constructable buildings.</param>
        private IEnumerable<GameLocation> GetLocations(bool buildingInteriors = true)
        {
            return this.GetLocationsWithInfo(buildingInteriors).Select(info => info.Location);
        }

        /// <summary>Get all locations in the game.</summary>
        /// <param name="buildingInteriors">Whether to also get the interior locations for constructable buildings.</param>
        private IEnumerable<LocationInfo> GetLocationsWithInfo(bool buildingInteriors = true)
        {
            // get available root locations
            IEnumerable<GameLocation> rootLocations = Game1.locations;
            if (SaveGame.loaded?.locations != null)
                rootLocations = rootLocations.Concat(SaveGame.loaded.locations);

            // yield root + child locations
            foreach (GameLocation location in rootLocations)
            {
                yield return new LocationInfo(location, null);

                if (buildingInteriors)
                {
                    foreach (Building building in location.buildings)
                    {
                        GameLocation indoors = building.indoors.Value;
                        if (indoors != null)
                            yield return new LocationInfo(indoors, building);
                    }
                }
            }
        }

        /// <summary>Remove a case-insensitive key from the paint mask cache.</summary>
        /// <param name="assetName">The paint mask asset name.</param>
        private bool RemoveFromPaintMaskCache(IAssetName assetName)
        {
            // make cache case-insensitive
            // This is needed for cache invalidation since mods may specify keys with a different capitalization
            if (!object.ReferenceEquals(BuildingPainter.paintMaskLookup.Comparer, StringComparer.OrdinalIgnoreCase))
                BuildingPainter.paintMaskLookup = new Dictionary<string, List<List<int>>>(BuildingPainter.paintMaskLookup, StringComparer.OrdinalIgnoreCase);

            // remove key from cache
            return BuildingPainter.paintMaskLookup.Remove(assetName.Name);
        }

        /// <summary>Metadata about a location used in asset propagation.</summary>
        private readonly struct LocationInfo
        {
            /*********
            ** Accessors
            *********/
            /// <summary>The location instance.</summary>
            public GameLocation Location { get; }

            /// <summary>The building which contains the location, if any.</summary>
            public Building ParentBuilding { get; }


            /*********
            ** Public methods
            *********/
            /// <summary>Construct an instance.</summary>
            /// <param name="location">The location instance.</param>
            /// <param name="parentBuilding">The building which contains the location, if any.</param>
            public LocationInfo(GameLocation location, Building parentBuilding)
            {
                this.Location = location;
                this.ParentBuilding = parentBuilding;
            }
        }
    }
}
