﻿using Barotrauma.Items.Components;
using Barotrauma.Networking;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Barotrauma
{
    partial class KarmaManager : ISerializableEntity
    {
        private class ClientMemory
        {
            public List<Pair<Wire, float>> WireDisconnectTime = new List<Pair<Wire, float>>();

            //the client's karma value when they were last sent a notification about it (e.g. "your karma is very low")
            public float PreviousNotifiedKarma;

            public float StructureDamageAccumulator;
            
            private float structureDamagePerSecond;
            public float StructureDamagePerSecond
            {
                get { return Math.Max(StructureDamageAccumulator, structureDamagePerSecond); }
                set { structureDamagePerSecond = value; }
            }
        }

        public bool TestMode = false;

        private readonly Dictionary<Client, ClientMemory> clientMemories = new Dictionary<Client, ClientMemory>();
        private readonly List<Client> bannedClients = new List<Client>();

        private DateTime perSecondUpdate;

        private double KarmaNotificationTime;

        public void UpdateClients(IEnumerable<Client> clients, float deltaTime)
        {
            if (!GameMain.Server.GameStarted) { return; }

            bannedClients.Clear();
            foreach (Client client in clients)
            {
                var clientMemory = GetClientMemory(client);
                UpdateClient(client, deltaTime);

                if (perSecondUpdate < DateTime.Now)
                {
                    clientMemory.StructureDamagePerSecond = clientMemory.StructureDamageAccumulator;
                    clientMemory.StructureDamageAccumulator = 0.0f;
                }
            }
            if (perSecondUpdate < DateTime.Now)
            {
                perSecondUpdate = DateTime.Now + new TimeSpan(0, 0, 1);
            }

            if (TestMode || Timing.TotalTime > KarmaNotificationTime)
            {
                foreach (Client client in clients)
                {
                    SendKarmaNotifications(client);
                }
                KarmaNotificationTime = Timing.TotalTime + KarmaNotificationInterval;
            }

            foreach (Client bannedClient in bannedClients)
            {
                GameMain.Server.BanClient(bannedClient, $"KarmaBanned~[banthreshold]={(int)KickBanThreshold}", duration: TimeSpan.FromSeconds(GameMain.Server.ServerSettings.AutoBanTime));
            }
        }

        private void SendKarmaNotifications(Client client, string debugKarmaChangeReason = "")
        {
            var clientMemory = GetClientMemory(client);
            float karmaChange = client.Karma - clientMemory.PreviousNotifiedKarma;
            if (Math.Abs(karmaChange) > KarmaNotificationInterval || (TestMode && Math.Abs(karmaChange) > 2.0f))
            {
                if (TestMode)
                {
                    string msg =
                        karmaChange < 0 ? $"Your karma has decreased to {client.Karma}" : $"Your karma has increased to {client.Karma}";
                    if (!string.IsNullOrEmpty(debugKarmaChangeReason))
                    {
                        msg += $". Reason: {debugKarmaChangeReason}";
                    }
                    GameMain.Server.SendDirectChatMessage(msg, client);
                }
                else if (Math.Abs(KickBanThreshold - client.Karma) < KarmaNotificationInterval)
                {
                    GameMain.Server.SendDirectChatMessage(TextManager.Get("KarmaBanWarning"), client);
                }
                else
                {
                    GameMain.Server.SendDirectChatMessage(TextManager.Get(karmaChange < 0 ? "KarmaDecreasedUnknownAmount" : "KarmaIncreasedUnknownAmount"), client);
                }
                clientMemory.PreviousNotifiedKarma = client.Karma;
            }
        }

        private void UpdateClient(Client client, float deltaTime)
        {
            if (client.Character != null && !client.Character.Removed)
            {
                if (client.Karma > KarmaDecayThreshold)
                {
                    client.Karma -= KarmaDecay * deltaTime;
                }
                else if (client.Karma < KarmaIncreaseThreshold)
                {
                    client.Karma += KarmaIncrease * deltaTime;
                }

                //increase the strength of the herpes affliction in steps instead of linearly
                //otherwise clients could determine their exact karma value from the strength
                float herpesStrength = 0.0f;
                if (client.Karma < 20)                
                    herpesStrength = 100.0f;                
                else if (client.Karma < 30)                
                    herpesStrength = 60.0f;                
                else if (client.Karma < 40.0f)
                    herpesStrength = 30.0f;
                
                var existingAffliction = client.Character.CharacterHealth.GetAffliction<AfflictionSpaceHerpes>("spaceherpes");
                if (existingAffliction == null && herpesStrength > 0.0f)
                {
                    client.Character.CharacterHealth.ApplyAffliction(null, new Affliction(herpesAffliction, herpesStrength));
                }
                else if (existingAffliction != null)
                {
                    existingAffliction.Strength = herpesStrength;
                    if (herpesStrength <= 0.0f)
                    {
                        client.Character.CharacterHealth.ReduceAffliction(null, "invertcontrols", 100.0f);
                    }
                }

                //check if the client has disconnected an excessive number of wires
                var clientMemory = GetClientMemory(client);
                if (clientMemory.WireDisconnectTime.Count > (int)AllowedWireDisconnectionsPerMinute)
                {
                    clientMemory.WireDisconnectTime.RemoveRange(0, clientMemory.WireDisconnectTime.Count - (int)AllowedWireDisconnectionsPerMinute);
                    if (clientMemory.WireDisconnectTime.All(w => Timing.TotalTime - w.Second < 60.0f))
                    {
                        float karmaDecrease = -WireDisconnectionKarmaDecrease;
                        //engineers don't lose as much karma for removing lots of wires
                        if (client.Character.Info?.Job.Prefab.Identifier == "engineer") { karmaDecrease *= 0.5f; }
                        AdjustKarma(client.Character, karmaDecrease, "Disconnected excessive number of wires");
                    }
                }                

                if (client.Character?.Info?.Job.Prefab.Identifier == "captain" && client.Character.SelectedConstruction != null)
                {
                    if (client.Character.SelectedConstruction.GetComponent<Steering>() != null)
                    {
                        AdjustKarma(client.Character, SteerSubKarmaIncrease * deltaTime, "Steering the sub");
                    }
                }
            }

            if (client.Karma < KickBanThreshold && client.Connection != GameMain.Server.OwnerConnection)
            {
                if (TestMode)
                {
                    client.Karma = 50.0f;
                    GameMain.Server.SendDirectChatMessage("BANNED! (not really because karma test mode is enabled)", client);
                }
                else
                {
                    bannedClients.Add(client);
                }
            }
        }

        public void OnClientDisconnected(Client client)
        {
            clientMemories.Remove(client);
        }

        public void OnCharacterHealthChanged(Character target, Character attacker, float damage, IEnumerable<Affliction> appliedAfflictions = null)
        {
            if (target == null || attacker == null) { return; }
            if (target == attacker) { return; }

            //damaging dead characters doesn't affect karma
            if (target.IsDead || target.Removed) { return; }

            bool isEnemy = target.AIController is EnemyAIController || target.TeamID != attacker.TeamID;
            if (GameMain.Server.TraitorManager?.Traitors != null)
            {
                if (GameMain.Server.TraitorManager.Traitors.Any(t => t.Character == target))
                {
                    //traitors always count as enemies
                    isEnemy = true;
                }
                if (GameMain.Server.TraitorManager.Traitors.Any(t => t.Character == attacker && t.CurrentObjective.IsEnemy(target)))
                {
                    //target counts as an enemy to the traitor
                    isEnemy = true;
                }
            }
            
            //attacking/healing clowns has a smaller effect on karma
            if (target.HasEquippedItem("clownmask") &&
                target.HasEquippedItem("clowncostume"))
            {
                damage *= 0.5f;
            }

            if (appliedAfflictions != null)
            {
                foreach (Affliction affliction in appliedAfflictions)
                {
                    if (MathUtils.NearlyEqual(affliction.Prefab.KarmaChangeOnApplied, 0.0f)) { continue; }
                    damage -= affliction.Prefab.KarmaChangeOnApplied * affliction.Strength; 
                }
            }

            if (isEnemy)
            {
                if (damage > 0)
                {
                    float karmaIncrease = damage * DamageEnemyKarmaIncrease;
                    if (attacker?.Info?.Job.Prefab.Identifier == "securityofficer") { karmaIncrease *= 2.0f; }
                    AdjustKarma(attacker, karmaIncrease, "Damaged enemy");
                }
            }
            else
            {
                if (damage > 0)
                {
                    AdjustKarma(attacker, -damage * DamageFriendlyKarmaDecrease, "Damaged friendly");
                }
                else
                {
                    float karmaIncrease = -damage * HealFriendlyKarmaIncrease;
                    if (attacker?.Info?.Job.Prefab.Identifier == "medicaldoctor") { karmaIncrease *= 2.0f; }
                    AdjustKarma(attacker, karmaIncrease, "Healed friendly");
                }
            }
        }
        

        public void OnStructureHealthChanged(Structure structure, Character attacker, float damageAmount)
        {
            if (attacker == null) { return; }
            //damaging/repairing ruin structures or enemy subs doesn't affect karma
            if (structure.Submarine == null || structure.Submarine.TeamID != attacker.TeamID)
            {
                return;
            }

            if (damageAmount > 0)
            {
                if (StructureDamageKarmaDecrease <= 0.0f) { return; }
                Client client = GameMain.Server.ConnectedClients.Find(c => c.Character == attacker);
                if (client != null)
                {
                    //cap the damage so the karma can't decrease by more than MaxStructureDamageKarmaDecreasePerSecond per second
                    var clientMemory = GetClientMemory(client);
                    clientMemory.StructureDamageAccumulator += damageAmount;
                    if (clientMemory.StructureDamagePerSecond + damageAmount >= MaxStructureDamageKarmaDecreasePerSecond / StructureDamageKarmaDecrease)
                    {
                        damageAmount -= (MaxStructureDamageKarmaDecreasePerSecond / StructureDamageKarmaDecrease) - clientMemory.StructureDamagePerSecond;
                        if (damageAmount <= 0.0f) { return; }
                    }
                }
                AdjustKarma(attacker, -damageAmount * StructureDamageKarmaDecrease, "Damaged structures");
            }
            else
            {
                float karmaIncrease = -damageAmount * StructureRepairKarmaIncrease;
                //mechanics get twice as much karma for repairing walls
                if (attacker.Info?.Job.Prefab.Identifier == "mechanic") { karmaIncrease *= 2.0f; }
                AdjustKarma(attacker, karmaIncrease, "Repaired structures");
            }
        }

        public void OnItemRepaired(Character character, Repairable repairable, float repairAmount)
        {
            float karmaIncrease = repairAmount * ItemRepairKarmaIncrease;
            if (repairable.HasRequiredSkills(character)) { karmaIncrease *= 2.0f; }
            AdjustKarma(character, karmaIncrease, "Repaired item");
        }

        public void OnReactorOverHeating(Character character, float deltaTime)
        {
            AdjustKarma(character, -ReactorOverheatKarmaDecrease * deltaTime, "Caused reactor to overheat");
        }

        public void OnReactorMeltdown(Character character)
        {
            AdjustKarma(character, -ReactorMeltdownKarmaDecrease, "Caused a reactor meltdown");
        }

        public void OnExtinguishingFire(Character character, float deltaTime)
        {
            AdjustKarma(character, ExtinguishFireKarmaIncrease * deltaTime, "Extinguished a fire");
        }

        public void OnWireDisconnected(Character character, Wire wire)
        {
            if (character == null || wire == null) { return; }
            Client client = GameMain.Server.ConnectedClients.Find(c => c.Character == character);
            if (client == null) { return; }

            if (!clientMemories.ContainsKey(client)) { clientMemories[client] = new ClientMemory(); }

            clientMemories[client].WireDisconnectTime.RemoveAll(w => w.First == wire);
            clientMemories[client].WireDisconnectTime.Add(new Pair<Wire, float>(wire, (float)Timing.TotalTime));
        }

        private ClientMemory GetClientMemory(Client client)
        {
            if (!clientMemories.ContainsKey(client))
            {
                clientMemories[client] = new ClientMemory()
                {
                    PreviousNotifiedKarma = client.Karma
                };
            }
            return clientMemories[client];
        }

        public void OnSpamFilterTriggered(Client client)
        {
            if (client != null)
            {
                client.Karma -= SpamFilterKarmaDecrease;
                SendKarmaNotifications(client, "Triggered the spam filter");
            }
        }

        private void AdjustKarma(Character target, float amount, string debugKarmaChangeReason = "")
        {
            if (target == null) { return; }

            Client client = GameMain.Server.ConnectedClients.Find(c => c.Character == target);
            if (client == null) { return; }

            //all penalties/rewards are halved when wearing a clown costume
            if (target.HasEquippedItem("clownmask") &&
                target.HasEquippedItem("clowncostume"))
            {
                amount *= 0.5f;
            }

            client.Karma += amount;
            if (TestMode)
            {
                SendKarmaNotifications(client, debugKarmaChangeReason);
            }
        }
    }
}