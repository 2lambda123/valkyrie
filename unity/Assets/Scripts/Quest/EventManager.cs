﻿using System.Collections.Generic;
using UnityEngine;
using Assets.Scripts.Content;
using Assets.Scripts.UI.Screens;
using ValkyrieTools;
using System.IO;
using System.Text.RegularExpressions;
using System;
using System.Linq;

// Class for managing quest events
public class EventManager
{
    // A dictionary of available events
    public Dictionary<string, Event> events;

    // events should display monster image when not null
    public Quest.Monster monsterImage;
    // events should display monster health if true
    public bool monsterHealth = false;

    // Stack of events to be triggered
    public Stack<Event> eventStack;

    public Game game;

    // Event currently open
    public Event currentEvent;

    public EventManager()
    {
        Init(null);
    }

    public EventManager(Dictionary<string, string> data)
    {
        Init(data);
    }

    public void Init(Dictionary<string, string> data)
    {
        game = Game.Get();

        // This is filled out later but is required for loading saves
        game.CurrentQuest.eManager = this;

        events = new Dictionary<string, Event>();
        eventStack = new Stack<Event>();

        // Find quest events
        foreach (KeyValuePair<string, QuestData.QuestComponent> kv in game.CurrentQuest.qd.components)
        {
            if (kv.Value is QuestData.Event)
            {
                // If the event is a monster type cast it
                if (kv.Value is QuestData.Spawn)
                {
                    events.Add(kv.Key, new MonsterEvent(kv.Key));
                }
                else if (kv.Value is QuestData.Token)
                {
                    events.Add(kv.Key, new Token(kv.Key));
                }
                else
                {
                    events.Add(kv.Key, new Event(kv.Key));
                }
            }
        }

        // Add game content perils as available events
        foreach (string perilKey in game.cd.Keys<PerilData>())
        {
            events.Add(perilKey, new Peril(perilKey));
        }

        if (data != null)
        {
            if (data.ContainsKey("queue"))
            {
                foreach (string s in data["queue"].Split(" ".ToCharArray(), System.StringSplitOptions.RemoveEmptyEntries))
                {
                    eventStack.Push(events[s]);
                }
            }
            if (data.ContainsKey("monsterimage"))
            {
                monsterImage = Quest.Monster.GetMonster(data["monsterimage"]);
            }
            if (data.ContainsKey("monsterhealth"))
            {
                bool.TryParse(data["monsterhealth"], out monsterHealth);
            }
            if (data.ContainsKey("currentevent") && game.CurrentQuest.activeShop != data["currentevent"])
            {
                currentEvent = events[data["currentevent"]];
                ResumeEvent();
            }
        }
    }

    // Queue all events by trigger, optionally start
    public bool EventTriggerType(string type, bool trigger=true)
    {
        bool queued = false;
        foreach (KeyValuePair<string, Event> kv in events)
        {
            if (kv.Value.qEvent != null && kv.Value.qEvent.trigger.Equals(type))
            {
                queued |= QueueEvent(kv.Key, trigger);
            }
        }

        return queued;
    }

    // Queue event, optionally trigger next event
    public bool QueueEvent(string name, bool trigger=true)
    {
        // Check if the event doesn't exists - quest fault
        if (!events.ContainsKey(name))
        {
            string questToTransition = game.CurrentQuest.originalPath + Path.DirectorySeparatorChar + name;
            if (game.CurrentQuest.fromSavegame)
            {
                questToTransition = ContentData.ValkyrieLoadQuestPath + Path.DirectorySeparatorChar + name;
            }
            if (File.Exists(questToTransition))
            {
                events.Add(name, new StartQuestEvent(name));
            }
            else
            {
                ValkyrieDebug.Log("Warning: Missing event called: " + name);
                game.CurrentQuest.log.Add(new Quest.LogEntry("Warning: Missing event called: " + name, true));
                return false;
            }
        }

        // Don't queue disabled events
        if (events[name].Disabled()) return false;

        // Place this on top of the stack
        eventStack.Push(events[name]);

        // IF there is a current event trigger if specified
        if (currentEvent == null && trigger)
        {
            TriggerEvent();
        }

        return true;
    }

    // Trigger next event in stack
    public void TriggerEvent()
    {
        Game game = Game.Get();
        // First check if things need to be added to the queue at end round
        if (game.roundControl.CheckNewRound()) return;

        // No events to trigger
        if (eventStack.Count == 0) return;

        // Get the next event
        Event e = eventStack.Pop();
        currentEvent = e;

        // Move to another quest
        if (e is StartQuestEvent)
        {
            // This loads the game
            game.CurrentQuest.ChangeQuest((e as StartQuestEvent).name);
            return;
        }

        // Event may have been disabled since added
        if (e.Disabled())
        {
            currentEvent = null;
            TriggerEvent();
            return;
        }

        // Play audio
        if (game.cd.TryGet(e.qEvent.audio, out AudioData audioData))
        {
            game.audioControl.Play(audioData.file);
        }
        else if (e.qEvent.audio.Length > 0)
        {
            game.audioControl.Play(Quest.FindLocalisedMultimediaFile(e.qEvent.audio, Path.GetDirectoryName(game.CurrentQuest.qd.questPath)));
        }

        // Set Music
        if (e.qEvent.music.Count > 0)
        {
            List<string> music = new List<string>();
            foreach (string s in e.qEvent.music)
            {
                if (game.cd.TryGet(s, out AudioData musicData))
                {
                    music.Add(musicData.file);
                }
                else
                {
                    music.Add(Quest.FindLocalisedMultimediaFile(s, Path.GetDirectoryName(game.CurrentQuest.qd.questPath)));
                }
            }
            game.audioControl.PlayMusic(music);
            if (music.Count > 0)
            {
                game.CurrentQuest.music = new List<string>(e.qEvent.music);
            }
        }

        // Perform var operations
        game.CurrentQuest.vars.Perform(e.qEvent.operations);
        // Update morale change
        if (game.gameType is D2EGameType)
        {
            game.CurrentQuest.AdjustMorale(0);
        }
        if (game.CurrentQuest.vars.GetValue("$restock") == 1)
        {
            game.CurrentQuest.GenerateItemSelection();
        }

        // If a dialog window is open we force it closed (this shouldn't happen)
        foreach (GameObject go in GameObject.FindGameObjectsWithTag(Game.DIALOG))
            UnityEngine.Object.Destroy(go);

        // If this is a monster event then add the monster group
        if (e is MonsterEvent)
        {
            MonsterEvent qe = (MonsterEvent)e;

            qe.MonsterEventSelection();

            // Is this type new?
            Quest.Monster oldMonster = null;
            foreach (Quest.Monster m in game.CurrentQuest.monsters)
            {
                if (m.monsterData.sectionName.Equals(qe.cMonster.sectionName))
                {
                    // Matched existing monster
                    oldMonster = m;
                }
            }

            // Add the new type
            if (!game.gameType.MonstersGrouped() || oldMonster == null)
            {
                var monster = new Quest.Monster(qe);
                game.CurrentQuest.monsters.Add(monster);
                game.monsterCanvas.UpdateList();
                // Update monster var
                game.CurrentQuest.vars.SetValue("#monsters", game.CurrentQuest.monsters.Count);
            }
            // There is an existing group, but now it is unique
            else if (qe.qMonster.unique)
            {
                oldMonster.unique = true;
                oldMonster.uniqueText = qe.qMonster.uniqueText;
                oldMonster.uniqueTitle = qe.GetUniqueTitle();
                oldMonster.healthMod = Mathf.RoundToInt(qe.qMonster.uniqueHealthBase + (Game.Get().CurrentQuest.GetHeroCount() * qe.qMonster.uniqueHealthHero));
            }

            // Display the location(s)
            if (qe.qEvent.locationSpecified && e.qEvent.display)
            {
                game.tokenBoard.AddMonster(qe);
            }
        }

        // Highlight a space on the board
        if (e.qEvent.highlight)
        {
            game.tokenBoard.AddHighlight(e.qEvent);
        }

        // Is this a shop?
        List<string> itemList = new List<string>();
        foreach (string s in e.qEvent.addComponents)
        {
            if (s.IndexOf("QItem") == 0)
            {
                // Fix #998
                if (game.gameType.TypeName() == "MoM" && itemList.Count==1)
                {
                    ValkyrieDebug.Log("WARNING: only one QItem can be used in event " + e.qEvent.sectionName + ", ignoring other items");
                    break;
                }
                itemList.Add(s);
            }
        }
        // Add board components
        game.CurrentQuest.Add(e.qEvent.addComponents, itemList.Count > 1);
        // Remove board components
        game.CurrentQuest.Remove(e.qEvent.removeComponents);

        // Move camera
        if (e.qEvent.locationSpecified && !(e.qEvent is QuestData.UI))
        {
            CameraController.SetCamera(e.qEvent.location);
        }

        if (e.qEvent is QuestData.Puzzle)
        {
            QuestData.Puzzle p = e.qEvent as QuestData.Puzzle;
            if (p.puzzleClass.Equals("slide"))
            {
                new PuzzleSlideWindow(e);
            }
            if (p.puzzleClass.Equals("code"))
            {
                new PuzzleCodeWindow(e);
            }
            if (p.puzzleClass.Equals("image"))
            {
                new PuzzleImageWindow(e);
            }
            if (p.puzzleClass.Equals("tower"))
            {
                new PuzzleTowerWindow(e);
            }
            return;
        }

        // Set camera limits
        if (e.qEvent.minCam)
        {
            CameraController.SetCameraMin(e.qEvent.location);
        }
        if (e.qEvent.maxCam)
        {
            CameraController.SetCameraMax(e.qEvent.location);
        }

        // Is this a shop?
        if (itemList.Count > 1 && !game.CurrentQuest.boardItems.ContainsKey("#shop"))
        {
            game.CurrentQuest.boardItems.Add("#shop", new ShopInterface(itemList, Game.Get(), e.qEvent.sectionName));
            game.CurrentQuest.ordered_boardItems.Add("#shop");
        }
        else if (!e.qEvent.display)
        {
            // Only raise dialog if there is text, otherwise auto confirm

            var firstEnabledButtonIndex = e.qEvent.buttons
                .TakeWhile(b => !IsButtonEnabled(b, game.CurrentQuest.vars))
                .Count();
            EndEvent(firstEnabledButtonIndex);
        }
        else
        {
            if (monsterImage != null)
            {
                MonsterDialogMoM.DrawMonster(monsterImage, true);
                if (monsterHealth)
                {
                }
            }
            new DialogWindow(e);
        }
    }

    private static bool IsButtonEnabled(QuestButtonData b, VarManager varManager)
    {
        return b.ConditionFailedAction == QuestButtonAction.NONE || !b.HasCondition || varManager.Test(b.Condition);
    }

    public void ResumeEvent()
    {
        Event e = currentEvent;
        if (e is MonsterEvent)
        {
            // Display the location(s)
            if (e.qEvent.locationSpecified && e.qEvent.display)
            {
                game.tokenBoard.AddMonster(e as MonsterEvent);
            }
        }

        // Highlight a space on the board
        if (e.qEvent.highlight)
        {
            game.tokenBoard.AddHighlight(e.qEvent);
        }

        if (e.qEvent is QuestData.Puzzle)
        {
            QuestData.Puzzle p = e.qEvent as QuestData.Puzzle;
            if (p.puzzleClass.Equals("slide"))
            {
                new PuzzleSlideWindow(e);
            }
            if (p.puzzleClass.Equals("code"))
            {
                new PuzzleCodeWindow(e);
            }
            if (p.puzzleClass.Equals("image"))
            {
                new PuzzleImageWindow(e);
            }
            return;
        }
        new DialogWindow(e);
    }

    // Event ended
    public void EndEvent(int state = 0)
    {
        EndEvent(currentEvent.qEvent, state);
    }

    // Event ended
    public void EndEvent(QuestData.Event eventData, int state=0)
    {
        // Get list of next events
        List<string> eventList = new List<string>();
        if (eventData.buttons.Count > state)
        {
            eventList = eventData.buttons[state].EventNames;
        }

        // Only take enabled events from list
        List<string> enabledEvents = new List<string>();
        foreach (string s in eventList)
        {
            // Check if the event doesn't exists - quest fault
            if (!events.ContainsKey(s))
            {
                string questToTransition = game.CurrentQuest.originalPath + Path.DirectorySeparatorChar + s;
                if (game.CurrentQuest.fromSavegame)
                {
                    questToTransition = ContentData.ValkyrieLoadQuestPath + Path.DirectorySeparatorChar + s;
                }
                if (File.Exists(questToTransition))
                {
                    events.Add(s, new StartQuestEvent(s));
                    enabledEvents.Add(s);
                }
                else
                {
                    ValkyrieDebug.Log("Warning: Missing event called: " + s);
                    game.CurrentQuest.log.Add(new Quest.LogEntry("Warning: Missing event called: " + s, true));
                }
            }
            else if (!game.CurrentQuest.eManager.events[s].Disabled())
            {
                enabledEvents.Add(s);
            }
        }

        // Has the quest ended?
        if (game.CurrentQuest.vars.GetValue("$end") != 0)
        {
            game.CurrentQuest.questHasEnded = true;

            if( Path.GetFileName(game.CurrentQuest.originalPath).StartsWith("EditorScenario") 
             || !Path.GetFileName(game.CurrentQuest.originalPath).EndsWith(".valkyrie") )
            {
                // do not show score screen for scenario with a non customized name, or if the scenario is not a package (most probably a test)
                GameStateManager.MainMenu();
            }
            else
            {
                new EndGameScreen();
            }
            
            return;
        }

        currentEvent = null;
        // Are there any events?
        if (enabledEvents.Count > 0)
        {
            // Are we picking at random?
            if (eventData.randomEvents)
            {
                // Add a random event
                game.CurrentQuest.eManager.QueueEvent(enabledEvents[UnityEngine.Random.Range(0, enabledEvents.Count)], false);
            }
            else
            {
                // Add the first valid event
                game.CurrentQuest.eManager.QueueEvent(enabledEvents[0], false);
            }
        }

        // Add any custom triggered events
        AddCustomTriggers();

        if (eventStack.Count == 0)
        {
            monsterImage = null;
            monsterHealth = false;
            if (game.CurrentQuest.phase == Quest.MoMPhase.monsters)
            {
                game.roundControl.MonsterActivated();
                return;
            }
        }

        // Trigger a stacked event
        TriggerEvent();
    }

    public void AddCustomTriggers()
    {
        foreach (KeyValuePair<string, float> kv in game.CurrentQuest.vars.GetPrefixVars("@"))
        {
            if (kv.Value > 0)
            {
                game.CurrentQuest.vars.SetValue(kv.Key, 0);
                EventTriggerType("Var" + kv.Key.Substring(1), false);
            }
        }
        foreach (KeyValuePair<string, float> kv in game.CurrentQuest.vars.GetPrefixVars("$@"))
        {
            if (kv.Value > 0)
            {
                game.CurrentQuest.vars.SetValue(kv.Key, 0);
                EventTriggerType("Var" + "$" + kv.Key.Substring(2), false);
            }
        }
    }

    // Event control class
    public class Event
    {
        private bool addsFallbackContinueButton;
        public Game game;
        public QuestData.Event qEvent;

        // Create event from quest data
        public Event(string name, bool addsFallbackContinueButton = true)
        {
            this.addsFallbackContinueButton = addsFallbackContinueButton;
            game = Game.Get();
            if (game.CurrentQuest.qd.components.ContainsKey(name))
            {
                qEvent = game.CurrentQuest.qd.components[name] as QuestData.Event;
            }
        }

        // Get the text to display for the event
        virtual public string GetText()
        {
            string text = qEvent.text.Translate(true);

            // Find and replace {q:element with the name of the
            // element

            text = ReplaceComponentText(text);

            // Find and replace rnd:hero with a hero
            // replaces all occurances with the one hero

            Quest.Hero h = game.CurrentQuest.GetRandomHero();
            if (text.Contains("{rnd:hero"))
            {
                h.selected = true;
            }
            text = text.Replace("{rnd:hero}", h.heroData.name.Translate());

            // Random heroes can have custom lookups
            if (text.StartsWith("{rnd:hero:"))
            {
                HeroData hero = h.heroData;
                int start = "{rnd:hero:".Length;
                if (!hero.ContainsTrait("male"))
                {
                    if (text[start] == '{')
                    {
                        start = text.IndexOf("}", start);
                    }
                    start = text.IndexOf(":{", start) + 1;
                    if (text[start] == '{')
                    {
                        start = text.IndexOf("}", start);
                    }
                    start = text.IndexOf(":", start) + 1;
                }
                int next = start;
                if (text[next] == '{')
                {
                    next = text.IndexOf("}", next);
                }
                next = text.IndexOf(":{", next) + 1;
                int end = next;
                if (text[end] == '{')
                {
                    end = text.IndexOf("}", end);
                }
                end = text.IndexOf(":", end);
                if (end < 0) end = text.Length - 1;
                string toReplace = text.Substring(next, end - next);
                text = new StringKey(text.Substring(start, (next - start) - 1)).Translate();
                text = text.Replace(toReplace, hero.name.Translate());
            }

            // Fix new lines and replace symbol text with special characters
            return OutputSymbolReplace(text).Replace("\\n", "\n");
        }

        public static string ReplaceComponentText(string input)
        {
            string toReturn = input;
            if (toReturn.Contains("{c:"))
            {
                Regex questItemRegex = new Regex("{c:(((?!{).)*?)}");
                string replaceFrom;
                string componentName;
                string componentText;
                foreach (Match oneVar in questItemRegex.Matches(toReturn))
                {
                    replaceFrom = oneVar.Value;                    
                    componentName = oneVar.Groups[1].Value;
                    QuestData.QuestComponent component;
                    if (Game.Get().CurrentQuest.qd.components.TryGetValue(componentName, out component))
                    {
                        componentText = getComponentText(component);
                        toReturn = toReturn.Replace(replaceFrom, componentText);
                    }
                }
            }
            return toReturn;
        }

        /// <summary>
        /// Get text related with selected component
        /// </summary>
        /// <param name="component">component to get text</param>
        /// <returns>extracted text</returns>
        public static string getComponentText(QuestData.QuestComponent component)
        {
            Game game = Game.Get();
            switch (component.GetType().Name)
            {
                case "Event":
                    if(!game.CurrentQuest.heroSelection.ContainsKey(component.sectionName) || game.CurrentQuest.heroSelection[component.sectionName].Count == 0)
                    {
                        return component.sectionName;
                    }
                    return game.CurrentQuest.heroSelection[component.sectionName][0].heroData.name.Translate();
                case "Tile":
                    // Replaced with the name of the Tile
                    return game.cd.Get<TileSideData>(((QuestData.Tile)component).tileSideName).name.Translate();
                case "CustomMonster":
                    // Replaced with the custom nonster name
                    return ((QuestData.CustomMonster)component).monsterName.Translate();
                case "Spawn":
                    if (!game.CurrentQuest.monsterSelect.ContainsKey(component.sectionName))
                    {
                        return component.sectionName;
                    }
                    // Replaced with the text shown in the spawn
                    string monsterName = game.CurrentQuest.monsterSelect[component.sectionName];
                    if (monsterName.StartsWith("Custom")) {
                        return ((QuestData.CustomMonster)game.CurrentQuest.qd.components[monsterName]).monsterName.Translate();
                    } else
                    {
                        var monsterData = game.cd.Get<MonsterData>(game.CurrentQuest.monsterSelect[component.sectionName]);
                        return monsterData.name.Translate();
                    }
                case "QItem":
                    if (!game.CurrentQuest.itemSelect.ContainsKey(component.sectionName))
                    {
                        return component.sectionName;
                    }
                    // Replaced with the first element in the list
                    var itemData = game.cd.Get<ItemData>(game.CurrentQuest.itemSelect[component.sectionName]);
                    return itemData.name.Translate();
                default:
                    return component.sectionName;
            }
        }

        public static explicit operator Event(QuestData.QuestComponent v)
        {
            throw new NotImplementedException();
        }

        public List<DialogWindow.EventButton> GetButtons()
        {
            List<DialogWindow.EventButton> buttons = new List<DialogWindow.EventButton>();

            // Determine if no buttons should be displayed
            if (!ButtonsPresent())
            {
                return buttons;
            }

            for (int i = 0; i < qEvent.buttons.Count; i++)
            {
                buttons.Add(new DialogWindow.EventButton(qEvent.buttons[i]));
            }

            // If there are no enabled buttons - add a default continue button
            var atLeastOneButtonActive = buttons.Any(b => b.action == QuestButtonAction.NONE 
                                                          || Game.Get().CurrentQuest.vars.Test(b.condition));
            if (addsFallbackContinueButton && !atLeastOneButtonActive)
            {
                buttons.Add(new DialogWindow.EventButton(new QuestButtonData(CommonStringKeys.CONTINUE)));
            }
            
            return buttons;
        }

        // Is the confirm button present?
        public bool ButtonsPresent()
        {
            // If the event can't be canceled it must have buttons
            if (!qEvent.cancelable) return true;
            // Check if any of the next events are enabled
            foreach (QuestButtonData l in qEvent.buttons)
            {
                foreach (string s in l.EventNames)
                {
                    // Check if the event doesn't exists - quest fault
                    if (!game.CurrentQuest.eManager.events.ContainsKey(s))
                    {
                        string questToTransition = game.CurrentQuest.originalPath + Path.DirectorySeparatorChar + s;
                        if (game.CurrentQuest.fromSavegame)
                        {
                            questToTransition = ContentData.ValkyrieLoadQuestPath + Path.DirectorySeparatorChar + s;
                        }
                        if (File.Exists(questToTransition))
                        {
                            game.CurrentQuest.eManager.events.Add(s, new StartQuestEvent(s));
                            return true;
                        }
                        else
                        {
                            ValkyrieDebug.Log("Warning: Missing event called: " + s);
                            game.CurrentQuest.log.Add(new Quest.LogEntry("Warning: Missing event called: " + s, true));
                            return false;
                        }
                    }
                    if (!game.CurrentQuest.eManager.events[s].Disabled()) return true;
                }
            }
            // Nothing valid, no buttons
            return false;
        }

        // Is this event disabled?
        virtual public bool Disabled()
        {
            if (game.debugTests)
                ValkyrieDebug.Log("Event test " + qEvent.sectionName + " result is : " + game.CurrentQuest.vars.Test(qEvent.tests));

            // check if condition is valid, and if there is something to do in this event (see #916)
            return (!game.CurrentQuest.vars.Test(qEvent.tests));
        }
    }

    public class StartQuestEvent : Event
    {
        public string name;

        public StartQuestEvent(string n) : base(n)
        {
            name = n;
        }

        override public bool Disabled()
        {
            return false;
        }
    }

    // Monster event extends event for adding monsters
    public class MonsterEvent : Event
    {
        public QuestData.Spawn qMonster;
        public MonsterData cMonster;

        public MonsterEvent(string name) : base(name)
        {
            // cast the monster event
            qMonster = qEvent as QuestData.Spawn;

            // monsters are generated on the fly to avoid duplicate for D2E when using random
        }

        public void MonsterEventSelection()
        {
            if (!game.CurrentQuest.RuntimeMonsterSelection(qMonster.sectionName))
            {
                ValkyrieDebug.Log("Warning: Monster type unknown in event: " + qMonster.sectionName);
                return;
            }
            string t = game.CurrentQuest.monsterSelect[qMonster.sectionName];
            if (game.CurrentQuest.qd.components.ContainsKey(t))
            {
                cMonster = new QuestMonster(game.CurrentQuest.qd.components[t] as QuestData.CustomMonster);
            }
            else
            {
                cMonster = game.cd.Get<MonsterData>(t);
            }
        }

        // Event text
        override public string GetText()
        {
            // Monster events have {type} replaced with the selected type
            return base.GetText().Replace("{type}", cMonster.name.Translate());
        }

        // Unique monsters can have a special name
        public StringKey GetUniqueTitle()
        {
            // Default to Master {type}
            if (qMonster.uniqueTitle.KeyExists())
            {
                return new StringKey("val", "MONSTER_MASTER_X", cMonster.name);
            }
            return new StringKey(qMonster.uniqueTitle,"{type}",cMonster.name.fullKey);
        }
    }

    // Peril extends event
    public class Peril : Event
    {
        public PerilData cPeril;

        public Peril(string name) : base(name)
        {
            // Event is pulled from content data not quest data
            cPeril = game.cd.Get<PerilData>(name);
            qEvent = cPeril;
        }
    }


    // Token event extends event for handling tokens
    public class Token : Event
    {
        public Token(string name) : base(name, false)
        {
        }
    }

    public override string ToString()
    {
        //Game game = Game.Get();
        string nl = System.Environment.NewLine;
        // General quest state block
        string r = "[EventManager]" + nl;
        r += "queue=";
        foreach (Event e in eventStack.ToArray())
        {
            r += e.qEvent.sectionName + " ";
        }
        r += nl;
        if (currentEvent != null)
        {
            r += "currentevent=" + currentEvent.qEvent.sectionName + nl;
        }
        if (monsterImage != null)
        {
            r += "monsterimage=" + monsterImage.GetIdentifier() + nl;
        }
        if (monsterHealth)
        {
            r += "monsterhealth=" + monsterHealth.ToString() + nl;
        }
        return r;
    }

    /// <summary>
    /// Replace symbol markers with special characters to be shown in Quest
    /// </summary>
    /// <param name="input">text to show</param>
    /// <returns></returns>
    public static string OutputSymbolReplace(string input)
    {
        string output = input;
        Game game = Game.Get();

        // Fill in variable data
        try
        {
            // Find first random number tag
            int index = output.IndexOf("{var:");
            // loop through event text
            while (index != -1)
            {
                // find end of tag
                string statement = output.Substring(index, output.IndexOf("}", index) + 1 - index);
                // Replace with variable data
                output = output.Replace(statement, game.CurrentQuest.vars.GetValue(statement.Substring(5, statement.Length - 6)).ToString());
                //find next random tag
                index = output.IndexOf("{var:");
            }
        }
        catch (System.Exception)
        {
            game.CurrentQuest.log.Add(new Quest.LogEntry("Warning: Invalid var clause in text: " + input, true));
        }

        foreach (var conversion in GetCharacterMap(false, true))
        {
            output = output.Replace(conversion.Key, conversion.Value);
        }

        return output;
    }

    /// <summary>
    /// Replace symbol markers with special characters to be stored in editor
    /// </summary>
    /// <param name="input">text to store</param>
    /// <returns></returns>
    public static string InputSymbolReplace(string input)
    {
        string output = input;

        foreach (var conversion in GetCharacterMap(false, true))
        {
            output = output.Replace(conversion.Value, conversion.Key);
        }

        return output;
    }

    public static Dictionary<string, string> GetCharacterMap(bool addRnd = false, bool addPacks = false)
    {
        if (!CHARS_MAP.ContainsKey(Game.Get().gameType.TypeName()))
        {
            return null;
        }
        Dictionary<string, string> toReturn = new Dictionary<string, string>(CHARS_MAP[Game.Get().gameType.TypeName()]);
        if (addRnd)
        {
            toReturn.Add("{rnd:hero}", "Rnd");
        }
        if (addPacks)
        {
            foreach (var entry in CHAR_PACKS_MAP[Game.Get().gameType.TypeName()])
            {
                toReturn.Add(entry.Key, entry.Value);
            }
        }
        return toReturn;
    }

    public static Dictionary<string, Dictionary<string, string>> CHARS_MAP = new Dictionary<string, Dictionary<string, string>>
    {
        { "D2E", new Dictionary<string,string>()
            {
                {"{heart}", "≥"},
                {"{fatigue}", "∏"},
                {"{might}", "∂"},
                {"{will}", "π"},
                {"{action}", "∞"},
                {"{knowledge}", "∑"},
                {"{awareness}", "μ"},
                {"{shield}", "≤"},
                {"{surge}", "±"},
            }
        },
        { "MoM", new Dictionary<string,string>()
            {
                {"{will}", ""},
                {"{action}", ""},
                {"{strength}", ""},
                {"{agility}", ""},
                {"{lore}", ""},
                {"{influence}", ""},
                {"{observation}", ""},
                {"{success}", ""},
                {"{clue}", ""},
            }
        },
        { "IA", new Dictionary<string,string>()
            {
                {"{action}", ""},
                {"{wound}", ""},
                {"{surge}", ""},
                {"{attack}", ""},
                {"{strain}", ""},
                {"{tech}", ""},
                {"{insight}", ""},
                {"{strength}", ""},
                {"{block}", ""},
                {"{evade}", ""},
                {"{dodge}", ""},
            }
        }
    };

    public static Dictionary<string, Dictionary<string, string>> CHAR_PACKS_MAP = new Dictionary<string, Dictionary<string, string>>
    {
        { "D2E", new Dictionary<string,string>()
        },
        { "MoM", new Dictionary<string,string>()
            {
                {"{MAD01}", ""},
                {"{MAD06}", ""},
                {"{MAD09}", ""},
                {"{MAD20}", ""},
                {"{MAD21}", ""},
                {"{MAD22}", ""},
                {"{MAD23}", ""},
                {"{MAD25}", ""},
                {"{MAD26}", ""},
                {"{MAD27}", ""},
                {"{MAD28}", ""},
            }
        },
        { "IA", new Dictionary<string,string>()
            {
                {"{SWI01}", ""},
            }
        }
    };
}

