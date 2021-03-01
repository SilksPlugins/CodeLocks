using CodeLocks.Configuration;
using CodeLocks.Locks;
using CodeLocks.UI;
using Rocket.API.Collections;
using Rocket.Core.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using SDG.Unturned;
using Steamworks;
using UnityEngine;
using ObjectManager = CodeLocks.Locks.ObjectManager;

namespace CodeLocks
{
    public class CodeLocksPlugin : RocketPlugin<CodeLocksConfiguration>
    {
        public static CodeLocksPlugin? Instance { get; private set; }

        private ObjectManager _objectManager = null!;
        public CodeLockManager CodeLockManager { get; private set; } = null!;
        private UIManager _uiManager = null!;

        private readonly List<Attempt> _attempts = new();

        protected override void Load()
        {
            Instance = this;

            var lazyCodeLockManager = new Lazy<CodeLockManager>(() => CodeLockManager!);

            _objectManager = new ObjectManager(lazyCodeLockManager);
            CodeLockManager = new CodeLockManager(_objectManager);
            _uiManager = new UIManager(Configuration.Instance);

            CodeLockManager.Load(Directory);
            _uiManager.Load();
            _objectManager.Load();

            UnturnedPatches.OnBarricadesSave += OnBarricadesSave;
            UnturnedPatches.OnCheckingDoorAccess += OnCheckingAccess;
            UnturnedPatches.OnCheckingStorageAccess += OnCheckingAccess;

            Level.onPostLevelLoaded += OnPostLevelLoaded;
            if (Level.isLoaded) OnPostLevelLoaded(0);
        }

        protected override void Unload()
        {
            // ReSharper disable DelegateSubtraction
            UnturnedPatches.OnBarricadesSave -= OnBarricadesSave;
            UnturnedPatches.OnCheckingDoorAccess -= OnCheckingAccess;
            UnturnedPatches.OnCheckingStorageAccess -= OnCheckingAccess;
            Level.onPostLevelLoaded -= OnPostLevelLoaded;
            // ReSharper restore DelegateSubtraction

            _uiManager.Unload();
            _objectManager.Unload();
            CodeLockManager.Save();

            Instance = null;
        }

        public override TranslationList DefaultTranslations { get; } = new TranslationList()
        {
            { "commands_codelock_help", "/lock <code> - Adds a code lock to an object, or changes if one already exists." },
            { "invalid_parameters", "<color=red>Invalid parameters.</color=red>" },

            { "commands_codelock_no_lockable_object", "<color=red>You are not looking at a lockable object.</color>" },
            { "commands_codelock_invalid_code", "<color=red>The given code ({0}) is invalid. The code must be four numbers (ex. 1234).</color>" },
            { "commands_codelock_code_added", "This object has been locked with the code {0}." },
            { "commands_codelock_code_changed", "This object's lock has been changed to the code {0}." },
            { "commands_codelock_code_removed", "This object's has been unlocked." },
            { "commands_codelock_no_access", "<color=red>You do not have access to this object's locks.</color>" },
            { "commands_codelock_no_code", "<color=red>There is no code lock on this object.</color>" },
        };

        private void OnBarricadesSave()
        {
            CodeLockManager.Save();
        }

        private void OnEnteredCode(Player player, CodeLockInfo codeLock, ushort enteredCode)
        {
            var steamId = player.channel.owner.playerID.steamID;

            if (codeLock.Code == enteredCode)
            {
                codeLock.Users.Add(steamId.m_SteamID);

                _attempts.RemoveAll(a => a.SteamId == steamId && a.InstanceId == codeLock.InstanceId);

                _objectManager.UpdateBarricade(codeLock, steamId);

                if (_objectManager.TryGetBarricade(codeLock.InstanceId,
                    out var x, out var y,
                    out var plant, out var index, out _,
                    out var drop) && drop.interactable is InteractableStorage)
                {
                    BarricadeManager.instance.askStoreStorage(steamId, x, y, plant, index, false);
                }
            }
            else
            {
                var attemptsConfig = Configuration.Instance.Attempts ?? new();

                _attempts.RemoveAll(a =>
                    a.SteamId == steamId
                    && a.InstanceId == codeLock.InstanceId
                    && Time.time - attemptsConfig.Cooldown > a.Time);

                _attempts.Add(new Attempt(steamId, codeLock.InstanceId));

                var count = _attempts.Count(a =>
                    a.SteamId == steamId
                    && a.InstanceId == codeLock.InstanceId);

                var damages = (attemptsConfig.Damages != null && attemptsConfig.Damages.Length > 0)
                    ? attemptsConfig.Damages : new byte[] { 0 };

                var damage = count > damages.Length ? damages[0] : attemptsConfig.Damages![count - 1];

                player.life.askDamage(damage, Vector3.up, EDeathCause.INFECTION, ELimb.SPINE, Provider.server, out _);
            }
        }

        private void OnCheckingAccess(CSteamID steamId, Interactable storage, ref bool intercept, ref bool shouldAllow)
        {
            BarricadeManager.tryGetInfo(storage.transform,
                out _, out _, out _, out _, out _,
                out var drop);

            var codeLock = CodeLockManager.GetCodeLock(drop.instanceID);
            if (codeLock == null) return;

            intercept = true;

            if (!codeLock.Users.Contains(steamId.m_SteamID))
            {
                var player = PlayerTool.getPlayer(steamId);
                if (player == null) return;

                _uiManager.ShowKeypad(player, codeLock, OnEnteredCode);

                shouldAllow = false;

                return;
            }

            intercept = true;
            shouldAllow = true;
        }

        private void OnPostLevelLoaded(int level)
        {
            var codeLocks = CodeLockManager.GetAllCodeLocks().ToList();

            foreach (var codeLock in codeLocks)
            {
                if (_objectManager.UpdateBarricade(codeLock)) continue;

                CodeLockManager.RemoveCodeLock(codeLock.InstanceId);
            }
        }
    }
}
