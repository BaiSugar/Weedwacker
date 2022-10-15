﻿using System.Collections.Concurrent;
using System.Timers;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using Weedwacker.GameServer.Enums;
using Weedwacker.GameServer.Systems.Avatar;
using Weedwacker.GameServer.Systems.Inventory;
using Weedwacker.GameServer.Systems.Player;
using Weedwacker.GameServer.Systems.Shop;
using Weedwacker.GameServer.Systems.Social;
using Weedwacker.Shared.Utils;

namespace Weedwacker.GameServer.Database
{
    internal static class DatabaseManager
    {
        static MongoClient DbClient;
        static IMongoDatabase Database;
        static IMongoCollection<Player> Players;
        static IMongoCollection<AvatarManager> Avatars;
        static IMongoCollection<InventoryManager> Inventories;
        static IMongoCollection<TeamManager> Teams;
        static IMongoCollection<ShopManager> Shops;
        static IMongoCollection<SocialManager> Social;
        static DatabaseProperties Properties;
        static readonly ReplaceOptions Replace = new() { IsUpsert = true };
        static readonly BulkWriteOptions BulkWrite = new() { IsOrdered = false };
        static System.Timers.Timer? UpdateTimer;

        // Aggregate update operations, and bulkWrite periodically
        static ConcurrentBag<WriteModel<Player>> PlayerWrites = new();
        static ConcurrentBag<WriteModel<AvatarManager>> AvatarWrites = new();
        static ConcurrentBag<WriteModel<InventoryManager>> InventoryWrites = new();
        static ConcurrentBag<WriteModel<TeamManager>> TeamWrites = new();
        static ConcurrentBag<WriteModel<ShopManager>> ShopWrites = new();
        static ConcurrentBag<WriteModel<SocialManager>> SocialWrites = new();

        public static async Task Initialize()
        {
            BsonSerializer.RegisterSerializer(new EnumSerializer<PlayerProperty>(BsonType.String));
            BsonSerializer.RegisterSerializer(new EnumSerializer<FightProperty>(BsonType.String));
            BsonSerializer.RegisterSerializer(new EnumSerializer<ItemType>(BsonType.String));

            DbClient = new MongoClient(GameServer.Configuration.Database.ConnectionUri);
            // Databases and collections are implicitly created
            Database = DbClient.GetDatabase(GameServer.Configuration.Database.Database);
            Players = Database.GetCollection<Player>("players");
            Avatars = Database.GetCollection<AvatarManager>("avatars");
            Inventories = Database.GetCollection<InventoryManager>("inventories");
            Teams = Database.GetCollection<TeamManager>("teams");
            Shops = Database.GetCollection<ShopManager>("shops");
            Social = Database.GetCollection<SocialManager>("social");

            if (Database.GetCollection<DatabaseProperties>("dbProperties").Find(w => true).FirstOrDefault() == null)
            {
                Properties = new();
                Database.GetCollection<DatabaseProperties>("dbProperties").InsertOne(Properties);
            }
            else
            {
                Properties = Database.GetCollection<DatabaseProperties>("dbProperties").Find(w => true).First();
            }

            Logger.WriteLine("Connected to GameServer database");

            // Create a timer with a five second interval.
            UpdateTimer = new System.Timers.Timer(5000);
            // Hook up the Elapsed event for the timer. 
            UpdateTimer.Elapsed += BulkUpdate;
            UpdateTimer.AutoReset = true;
            UpdateTimer.Enabled = true;
        }

        private static async void BulkUpdate(object? source, ElapsedEventArgs e)
        {
            await Task.WhenAll(new Task[]
            {
                new Task(() => {if (PlayerWrites.Any()) Players.BulkWriteAsync(PlayerWrites, BulkWrite); PlayerWrites.Clear(); }),
                new Task(() => {if (AvatarWrites.Any()) Avatars.BulkWriteAsync(AvatarWrites, BulkWrite); AvatarWrites.Clear(); }),
                new Task(() => {if (InventoryWrites.Any()) Inventories.BulkWriteAsync(InventoryWrites, BulkWrite); InventoryWrites.Clear(); }),
                new Task(() => {if (TeamWrites.Any()) Teams.BulkWriteAsync(TeamWrites, BulkWrite); TeamWrites.Clear(); }),
                new Task(() => {if (ShopWrites.Any()) Shops.BulkWriteAsync(ShopWrites, BulkWrite); ShopWrites.Clear(); }),
                new Task(() => {if (SocialWrites.Any()) Social.BulkWriteAsync(SocialWrites, BulkWrite); SocialWrites.Clear(); }),
            });
        }

        public static async Task<Player?> CreatePlayerFromAccountUidAsync(string accountUid, string heroName = "", int gameUid = 0)
        {
            //Make sure there are no name or id collisions
            var queryResult = await Players.FindAsync(w => w.Profile.HeroName == heroName || w.AccountUid == accountUid || w.GameUid == gameUid);
            if (queryResult.ToList().Count > 0)
            {
                return null;
            }

            // Account

            if (gameUid == Properties.NextGameUid || gameUid == 0)
            {
                //Increment the counter
                var filter = Builders<DatabaseProperties>.Filter.Eq(x => x.NextGameUid, Properties.NextGameUid); // All the documents with (NextUid = Properties.NextUid)
                var update = Builders<DatabaseProperties>.Update.Inc(x => x.NextGameUid, 1); // Increment counter by 1
                gameUid = Properties.NextGameUid++;
                await Database.GetCollection<DatabaseProperties>("dbProperties").UpdateOneAsync(filter, update);
            }
            Player player = new(heroName, accountUid, gameUid);
            await Players.InsertOneAsync(player);
            return player;
        }

        public static async Task SavePlayerAsync(Player player)
        {
            // Replaces the player document.
            await Players.ReplaceOneAsync<Player>(w => w.AccountUid == player.AccountUid, player);
        }

        public static Task UpdatePlayerAsync(FilterDefinition<Player> filter, UpdateDefinition<Player> update)
        {
            PlayerWrites.Add(new UpdateOneModel<Player>(filter, update));
            return Task.CompletedTask;
        }

        public static async Task<Player?> GetPlayerByAccountUidAsync(string uid)
        {
            var matches = await Players.FindAsync(w => w.AccountUid == uid);
            var player = await matches.FirstOrDefaultAsync() ?? await CreatePlayerFromAccountUidAsync(uid);
            //Attach player systems to the player
            player.Avatars = await GetAvatarsByPlayerAsync(player) ?? await SaveAvatarsAsync(new AvatarManager(player)); // Load avatars before inventory, so that we can attach weapons while loading them
            player.Inventory = await GetInventoryByPlayerAsync(player) ?? await SaveInventoryAsync(new InventoryManager(player));
            player.Inventory.ShopManager = await GetShopsByPlayerAsync(player) ?? await SaveShopsAsync(new ShopManager(player));
            player.SocialManager = await GetSocialByPlayerAsync(player) ?? await SaveSocialAsync(new SocialManager(player));
            player.TeamManager = await GetTeamsByPlayerAsync(player) ?? await SaveTeamsAsync(new TeamManager(player));
            await player.OnLoadAsync();

            return player;
        }

        public static async Task<AvatarManager> SaveAvatarsAsync(AvatarManager avatars)
        {
            await Avatars.ReplaceOneAsync<AvatarManager>(w => w.OwnerId == avatars.OwnerId, avatars, Replace);
            return avatars;
        }

        public static Task UpdateAvatarsAsync(FilterDefinition<AvatarManager> filter, UpdateDefinition<AvatarManager> update)
        {
            AvatarWrites.Add(new UpdateOneModel<AvatarManager>(filter, update));
            return Task.CompletedTask;
        }

        private static async Task<AvatarManager?> GetAvatarsByPlayerAsync(Player owner)
        {
            AvatarManager avatars = await Database.GetCollection<AvatarManager>("avatars").Find(w => w.OwnerId == owner.GameUid).FirstOrDefaultAsync();
            if (avatars != null) await avatars.OnLoadAsync(owner);
            return avatars;
        }
        public static async Task<InventoryManager> SaveInventoryAsync(InventoryManager inventory)
        {
            await Inventories.ReplaceOneAsync<InventoryManager>(w => w.OwnerId == inventory.OwnerId, inventory, Replace);
            return inventory;
        }
        private static async Task<InventoryManager?> GetInventoryByPlayerAsync(Player owner)
        {
            InventoryManager inventory = await Database.GetCollection<InventoryManager>("inventories").Find(w => w.OwnerId == owner.GameUid).FirstOrDefaultAsync();
            if (inventory != null) await inventory.OnLoadAsync(owner);
            return inventory;
        }

        public static Task UpdateInventoryAsync(FilterDefinition<InventoryManager> filter, UpdateDefinition<InventoryManager> update)
        {
            InventoryWrites.Add(new UpdateOneModel<InventoryManager>(filter, update));
            return Task.CompletedTask;
        }

        public static async Task<TeamManager> SaveTeamsAsync(TeamManager teams)
        {
            await Teams.ReplaceOneAsync<TeamManager>(w => w.OwnerId == teams.OwnerId, teams, Replace);
            return teams;
        }

        public static Task UpdateTeamsAsync(FilterDefinition<TeamManager> filter, UpdateDefinition<TeamManager> update)
        {
            TeamWrites.Add(new UpdateOneModel<TeamManager>(filter, update));
            return Task.CompletedTask;
        }

        private static async Task<TeamManager?> GetTeamsByPlayerAsync(Player owner)
        {
            TeamManager teams = await Database.GetCollection<TeamManager>("teams").Find(w => w.OwnerId == owner.GameUid).FirstOrDefaultAsync();
            if (teams != null) await teams.OnLoadAsync(owner);
            return teams;
        }

        public static async Task<ShopManager> SaveShopsAsync(ShopManager shops)
        {
            await Shops.ReplaceOneAsync<ShopManager>(w => w.OwnerId == shops.OwnerId, shops, Replace);
            return shops;
        }

        public static Task UpdateShopsAsync(FilterDefinition<ShopManager> filter, UpdateDefinition<ShopManager> update)
        {
            ShopWrites.Add(new UpdateOneModel<ShopManager>(filter, update));
            return Task.CompletedTask;
        }

        private static async Task<ShopManager?> GetShopsByPlayerAsync(Player owner)
        {
            ShopManager shops = await Database.GetCollection<ShopManager>("shops").Find(w => w.OwnerId == owner.GameUid).FirstOrDefaultAsync();
            if (shops != null) await shops.OnLoadAsync(owner);
            return shops;
        }

        public static async Task<SocialManager> SaveSocialAsync(SocialManager social)
        {
            await Social.ReplaceOneAsync<SocialManager>(w => w.OwnerId == social.OwnerId, social);
            return social;
        }

        public static Task UpdateSocialAsync(FilterDefinition<SocialManager> filter, UpdateDefinition<SocialManager> update)
        {
            SocialWrites.Add(new UpdateOneModel<SocialManager>(filter, update));
            return Task.CompletedTask;
        }

        private static async Task<SocialManager?> GetSocialByPlayerAsync(Player owner)
        {
            SocialManager social = await Database.GetCollection<SocialManager>("social").Find(w => w.OwnerId == owner.GameUid).FirstOrDefaultAsync();
            if (social != null) await social.OnLoadAsync(owner);
            return social;
        }
    }
}
