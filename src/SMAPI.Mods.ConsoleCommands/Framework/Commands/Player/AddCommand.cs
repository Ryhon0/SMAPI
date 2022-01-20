using System;
using System.Linq;
using StardewValley;
using Object = StardewValley.Object;

namespace StardewModdingAPI.Mods.ConsoleCommands.Framework.Commands.Player
{
    /// <summary>A command which adds an item to the player inventory.</summary>
    internal class AddCommand : ConsoleCommand
    {
        /*********
        ** Fields
        *********/
        /// <summary>Provides methods for searching and constructing items.</summary>
        private readonly ItemRepository Items = new();


        /*********
        ** Public methods
        *********/
        /// <summary>Construct an instance.</summary>
        public AddCommand()
            : base("player_add", AddCommand.GetDescription()) { }

        /// <summary>Handle the command.</summary>
        /// <param name="monitor">Writes messages to the console and log file.</param>
        /// <param name="command">The command name.</param>
        /// <param name="args">The command arguments.</param>
        public override void Handle(IMonitor monitor, string command, ArgumentParser args)
        {
            // validate
            if (!Context.IsWorldReady)
            {
                monitor.Log("You need to load a save to use this command.", LogLevel.Error);
                return;
            }

            // read arguments
            if (!this.TryReadArguments(args, out string id, out string name, out int? count, out int? quality))
                return;

            // find matching item
            SearchableItem match = id != null
                ? this.FindItemByID(monitor, id)
                : this.FindItemByName(monitor, name);
            if (match == null)
                return;

            // apply count
            match.Item.Stack = count ?? 1;

            // apply quality
            if (quality != null)
            {
                if (match.Item is Object obj)
                    obj.Quality = quality.Value;
                else if (match.Item is Tool tool && args.Count >= 3)
                    tool.UpgradeLevel = quality.Value;
            }

            // add to inventory
            Game1.player.addItemByMenuIfNecessary(match.Item);
            monitor.Log($"OK, added {match.Name} (ID: {match.QualifiedItemID}) to your inventory.", LogLevel.Info);
        }


        /*********
        ** Private methods
        *********/
        private bool TryReadArguments(ArgumentParser args, out string id, out string name, out int? count, out int? quality)
        {
            // get id or 'name' flag
            if (!args.TryGet(0, "id or 'name'", out id, required: true))
            {
                name = null;
                count = null;
                quality = null;
                return false;
            }

            // get name
            int argOffset = 0;
            if (string.Equals(id, "name", StringComparison.OrdinalIgnoreCase))
            {
                id = null;
                if (!args.TryGet(1, "item name", out name))
                {
                    count = null;
                    quality = null;
                    return false;
                }

                argOffset = 1;
            }
            else
                name = null;

            // get count
            count = null;
            if (args.TryGetInt(1 + argOffset, "count", out int rawCount, min: 1, required: false))
                count = rawCount;

            // get quality
            quality = null;
            if (args.TryGetInt(2 + argOffset, "quality", out int rawQuality, min: Object.lowQuality, max: Object.bestQuality, required: false))
                quality = rawQuality;

            return true;
        }


        /// <summary>Get a matching item by its ID.</summary>
        /// <param name="monitor">Writes messages to the console and log file.</param>
        /// <param name="id">The qualified item ID.</param>
        private SearchableItem FindItemByID(IMonitor monitor, string id)
        {
            var item = this.Items
                .GetAll()
                .Where(p => string.Equals(p.QualifiedItemID, id, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(p => p.QualifiedItemID == id) // prefer case-sensitive match
                .FirstOrDefault();

            if (item == null)
                monitor.Log($"There's no item with the qualified ID {id}.", LogLevel.Error);

            return item;
        }

        /// <summary>Get a matching item by its name.</summary>
        /// <param name="monitor">Writes messages to the console and log file.</param>
        /// <param name="name">The partial item name to match.</param>
        private SearchableItem FindItemByName(IMonitor monitor, string name)
        {
            SearchableItem[] matches = this.Items.GetAll().Where(p => p.NameContains(name)).ToArray();
            if (!matches.Any())
            {
                monitor.Log($"There's no item with name '{name}'. You can use the 'list_items [name]' command to search for items.", LogLevel.Error);
                return null;
            }

            // handle single exact match
            SearchableItem[] exactMatches = matches.Where(p => p.NameEquivalentTo(name)).ToArray();
            if (exactMatches.Length == 1)
                return exactMatches[0];

            // handle ambiguous results
            string options = this.GetTableString(
                data: matches,
                header: new[] { "type", "name", "command" },
                getRow: item => new[] { item.Type.ToString(), item.DisplayName, $"player_add {item.QualifiedItemID}" }
            );
            monitor.Log($"There's no item with name '{name}'. Do you mean one of these?\n\n{options}", LogLevel.Info);
            return null;
        }

        /// <summary>Get the command description.</summary>
        private static string GetDescription()
        {
            return "Gives the player an item.\n"
                + "\n"
                + "Usage: player_add <item id> [count] [quality]\n"
                + "- item id: the item ID (use the 'list_items' command to see a list).\n"
                + "- count (optional): how many of the item to give.\n"
                + $"- quality (optional): one of {Object.lowQuality} (normal), {Object.medQuality} (silver), {Object.highQuality} (gold), or {Object.bestQuality} (iridium).\n"
                + "\n"
                + "Usage: player_add name \"<item name>\" [count] [quality]\n"
                + "- item name: the item name to search (use the 'list_items' command to see a list). This will add the item immediately if it's an exact match, else show a table of matching items.\n"
                + "- count (optional): how many of the item to give.\n"
                + $"- quality (optional): one of {Object.lowQuality} (normal), {Object.medQuality} (silver), {Object.highQuality} (gold), or {Object.bestQuality} (iridium).\n"
                + "\n"
                + "These examples both add the galaxy sword to your inventory:\n"
                + "  player_add weapon 4\n"
                + "  player_add name \"Galaxy Sword\"";
        }
    }
}
