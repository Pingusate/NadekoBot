﻿using Discord.Commands;
using Discord.Modules;
using NadekoBot.Classes;
using NadekoBot.Classes._DataModels;
using NadekoBot.Classes.Permissions;
using NadekoBot.Extensions;
using NadekoBot.Modules.Pokemon.PokeTypes;
using NadekoBot.Modules.Pokemon.PokeTypes.Extensions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace NadekoBot.Modules.Pokemon
{
    class PokemonModule : DiscordModule
    {
        public override string Prefix { get; } = NadekoBot.Config.CommandPrefixes.Pokemon;

        private ConcurrentDictionary<ulong, PokeStats> Stats = new ConcurrentDictionary<ulong, PokeStats>();

        public PokemonModule()
        {
            DbHandler.Instance.DeleteAll<PokeMoves>();
            DbHandler.Instance.InsertMany(
                DefaultMoves.DefaultMovesList.Select(move => new PokeMoves
                {
                    move = move.Key,
                    type = move.Value
                }));
        }

        private int GetDamage(PokeType usertype, PokeType targetType)
        {
            var rng = new Random();
            int damage = rng.Next(40, 60);
            var multiplier = usertype.Multiplier(targetType);
            damage = (int)(damage * multiplier);
            return damage;
        }

        private PokeType GetPokeType(ulong id)
        {

            var db = DbHandler.Instance.GetAllRows<UserPokeTypes>();
            Dictionary<long, int> setTypes = db.ToDictionary(x => x.UserId, y => y.type);
            if (setTypes.ContainsKey((long)id))
            {
                return PokemonTypesMain.IntToPokeType(setTypes[(long)id]);
            }

            int remainder = Math.Abs((int)(id % 18));

            return PokemonTypesMain.IntToPokeType(remainder);
        }

        public override void Install(ModuleManager manager)
        {
            manager.CreateCommands("", cgb =>
            {
                cgb.AddCheck(PermissionChecker.Instance);

                commands.ForEach(cmd => cmd.Init(cgb));

                cgb.CreateCommand(Prefix + "attack")
                    .Description("Attacks a target with the given move")
                    .Parameter("move", ParameterType.Required)
                    .Parameter("target", ParameterType.Unparsed)
                    .Do(async e =>
                    {
                        var move = e.GetArg("move");
                        var targetStr = e.GetArg("target")?.Trim();
                        if (string.IsNullOrWhiteSpace(targetStr))
                            return;
                        var target = e.Server.FindUsers(targetStr).FirstOrDefault();
                        if (target == null)
                        {
                            await e.Channel.SendMessage("No such person.");
                            return;
                        }
                        else if (target == e.User)
                        {
                            await e.Channel.SendMessage("You can't attack yourself.");
                            return;
                        }
                        // Checking stats first, then move
                        //Set up the userstats
                        PokeStats userStats;
                        userStats = Stats.GetOrAdd(e.User.Id, new PokeStats());

                        //Check if able to move
                        //User not able if HP < 0, has made more than 4 attacks
                        if (userStats.Hp < 0)
                        {
                            await e.Channel.SendMessage($"{e.User.Mention} has fainted and was not able to move!");
                            return;
                        }
                        if (userStats.MovesMade >= 5)
                        {
                            await e.Channel.SendMessage($"{e.User.Mention} has used too many moves in a row and was not able to move!");
                            return;
                        }
                        if (userStats.LastAttacked.Contains(target.Id))
                        {
                            await e.Channel.SendMessage($"{e.User.Mention} can't attack again without retaliation!");
                            return;
                        }
                        //get target stats
                        PokeStats targetStats;
                        targetStats = Stats.GetOrAdd(target.Id, new PokeStats());

                        //If target's HP is below 0, no use attacking
                        if (targetStats.Hp <= 0)
                        {
                            await e.Channel.SendMessage($"{target.Mention} has already fainted!");
                            return;
                        }

                        //Check whether move can be used
                        PokeType userType = GetPokeType(e.User.Id);

                        var enabledMoves = userType.GetMoves();
                        if (!enabledMoves.Contains(move.ToLowerInvariant()))
                        {
                            await e.Channel.SendMessage($"{e.User.Mention} was not able to use **{move}**, use {Prefix}listmoves to see moves you can use");
                            return;
                        }

                        //get target type
                        PokeType targetType = GetPokeType(target.Id);
                        //generate damage
                        int damage = GetDamage(userType, targetType);
                        //apply damage to target
                        targetStats.Hp -= damage;

                        var response = $"{e.User.Mention} used **{move}**{userType.Image} on {target.Mention}{targetType.Image} for **{damage}** damage";

                        //Damage type
                        if (damage < 40)
                        {
                            response += "\nIt's not effective..";
                        }
                        else if (damage > 60)
                        {
                            response += "\nIt's super effective!";
                        }
                        else
                        {
                            response += "\nIt's somewhat effective";
                        }

                        //check fainted

                        if (targetStats.Hp <= 0)
                        {
                            response += $"\n**{target.Name}** has fainted!";
                        }
                        else
                        {
                            response += $"\n**{target.Name}** has {targetStats.Hp} HP remaining";
                        }

                        //update other stats
                        userStats.LastAttacked.Add(target.Id);
                        userStats.MovesMade++;
                        targetStats.MovesMade = 0;
                        if (targetStats.LastAttacked.Contains(e.User.Id))
                        {
                            targetStats.LastAttacked.Remove(e.User.Id);
                        }

                        //update dictionary
                        //This can stay the same right?
                        Stats[e.User.Id] = userStats;
                        Stats[target.Id] = targetStats;

                        await e.Channel.SendMessage(response);
                    });

                cgb.CreateCommand(Prefix + "ml")
                    .Alias("movelist")
                    .Description("Lists the moves you are able to use")
                    .Do(async e =>
                    {
                        var userType = GetPokeType(e.User.Id);
                        List<string> movesList = userType.GetMoves();
                        var str = $"**Moves for `{userType.Name}` type.**";
                        foreach (string m in movesList)
                        {
                            str += $"\n{userType.Image}{m}";
                        }
                        await e.Channel.SendMessage(str);
                    });

                cgb.CreateCommand(Prefix + "heal")
                    .Description($"Heals someone. Revives those that fainted. Costs a NadekoFlower \n**Usage**:{Prefix}revive @someone")
                    .Parameter("target", ParameterType.Unparsed)
                    .Do(async e =>
                    {
                        var targetStr = e.GetArg("target")?.Trim();
                        if (string.IsNullOrWhiteSpace(targetStr))
                            return;
                        var usr = e.Server.FindUsers(targetStr).FirstOrDefault();
                        if (usr == null)
                        {
                            await e.Channel.SendMessage("No such person.");
                            return;
                        }
                        if (Stats.ContainsKey(usr.Id))
                        {

                            var targetStats = Stats[usr.Id];
                            int HP = targetStats.Hp;
                            if (targetStats.Hp == targetStats.MaxHp)
                            {
                                await e.Channel.SendMessage($"{usr.Name} already has full HP!");
                                return;
                            }
                            //Payment~
                            var amount = 1;
                            var pts = Classes.DbHandler.Instance.GetStateByUserId((long)e.User.Id)?.Value ?? 0;
                            if (pts < amount)
                            {
                                await e.Channel.SendMessage($"{e.User.Mention} you don't have enough {NadekoBot.Config.CurrencyName}s! \nYou still need {amount - pts} to be able to do this!");
                                return;
                            }
                            var target = (usr.Id == e.User.Id) ? "yourself" : usr.Name;
                            FlowersHandler.RemoveFlowers(e.User, $"Poke-Heal {target}", amount);
                            //healing
                            targetStats.Hp = targetStats.MaxHp;
                            if (HP < 0)
                            {
                                //Could heal only for half HP?
                                Stats[usr.Id].Hp = (targetStats.MaxHp / 2);
                                await e.Channel.SendMessage($"{e.User.Name} revived {usr.Name} with one {NadekoBot.Config.CurrencySign}");
                                return;
                            }
                            await e.Channel.SendMessage($"{e.User.Name} healed {usr.Name} for {targetStats.MaxHp - HP} HP with a 🌸");
                            return;
                        }
                        else
                        {
                            await e.Channel.SendMessage($"{usr.Name} already has full HP!");
                        }
                    });

                cgb.CreateCommand(Prefix + "type")
                    .Description($"Get the poketype of the target.\n**Usage**: {Prefix}type @someone")
                    .Parameter("target", ParameterType.Unparsed)
                    .Do(async e =>
                    {
                        var usrStr = e.GetArg("target")?.Trim();
                        if (string.IsNullOrWhiteSpace(usrStr))
                            return;
                        var usr = e.Server.FindUsers(usrStr).FirstOrDefault();
                        if (usr == null)
                        {
                            await e.Channel.SendMessage("No such person.");
                            return;
                        }
                        var pType = GetPokeType(usr.Id);
                        await e.Channel.SendMessage($"Type of {usr.Name} is **{pType.Name.ToLowerInvariant()}**{pType.Image}");

                    });

                cgb.CreateCommand(Prefix + "settype")
                    .Description($"Set your poketype. Costs a {NadekoBot.Config.CurrencyName}.\n**Usage**: {Prefix}settype fire")
                    .Parameter("targetType", ParameterType.Unparsed)
                    .Do(async e =>
                    {
                        var targetTypeStr = e.GetArg("targetType")?.ToUpperInvariant();
                        if (string.IsNullOrWhiteSpace(targetTypeStr))
                            return;
                        var targetType = PokemonTypesMain.stringToPokeType(targetTypeStr);
                        if (targetType == null)
                        {
                            await e.Channel.SendMessage("Invalid type specified. Type must be one of:\nNORMAL, FIRE, WATER, ELECTRIC, GRASS, ICE, FIGHTING, POISON, GROUND, FLYING, PSYCHIC, BUG, ROCK, GHOST, DRAGON, DARK, STEEL");
                            return;
                        }
                        if (targetType == GetPokeType(e.User.Id))
                        {
                            await e.Channel.SendMessage($"Your type is already {targetType.Name.ToLowerInvariant()}{targetType.Image}");
                            return;
                        }

                        //Payment~
                        var amount = 1;
                        var pts = DbHandler.Instance.GetStateByUserId((long)e.User.Id)?.Value ?? 0;
                        if (pts < amount)
                        {
                            await e.Channel.SendMessage($"{e.User.Mention} you don't have enough {NadekoBot.Config.CurrencyName}s! \nYou still need {amount - pts} to be able to do this!");
                            return;
                        }
                        FlowersHandler.RemoveFlowers(e.User, $"set usertype to {targetTypeStr}", amount);
                        //Actually changing the type here
                        var preTypes = DbHandler.Instance.GetAllRows<UserPokeTypes>();
                        Dictionary<long, int> Dict = preTypes.ToDictionary(x => x.UserId, y => y.Id);
                        if (Dict.ContainsKey((long)e.User.Id))
                        {
                            //delete previous type
                            DbHandler.Instance.Delete<UserPokeTypes>(Dict[(long)e.User.Id]);
                        }

                        DbHandler.Instance.InsertData(new UserPokeTypes
                        {
                            UserId = (long)e.User.Id,
                            type = targetType.Num
                        });

                        //Now for the response

                        await e.Channel.SendMessage($"Set type of {e.User.Mention} to {targetTypeStr}{targetType.Image} for a {NadekoBot.Config.CurrencySign}");
                    });
            });
        }
    }
}




