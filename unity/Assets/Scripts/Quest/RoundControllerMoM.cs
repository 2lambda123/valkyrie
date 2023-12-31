﻿using System.Collections.Generic;
using Assets.Scripts.Content;
using UnityEngine;

// This round controller extends the standard controller for MoM specific round order
public class RoundControllerMoM : RoundController
{
    bool endRoundRequested = false;

    // Investigators have finished
    override public void HeroActivated()
    {
        Game game = Game.Get();

        // Mark all Investigators as finished
        for (int i = 0; i < game.CurrentQuest.heroes.Count; i++)
        {
            game.CurrentQuest.heroes[i].activated = true;
        }

        if (game.CurrentQuest.vars.GetValue("#eliminatedprev") > 0 &&
            game.CurrentQuest.vars.GetValue("#eliminatedcomplete") <= 0)
        {
            game.CurrentQuest.vars.SetValue("#eliminatedcomplete", 1);
            if (game.CurrentQuest.eManager.EventTriggerType("Eliminated", false))
            {
                game.CurrentQuest.eManager.TriggerEvent();
                return;
            }
        }

        game.CurrentQuest.phase = Quest.MoMPhase.mythos;
        game.stageUI.Update();
        game.monsterCanvas.UpdateList();

        game.CurrentQuest.eManager.EventTriggerType("BeforeMonsterActivation", false);
        game.CurrentQuest.eManager.EventTriggerType("Mythos", false);
        game.CurrentQuest.eManager.EventTriggerType("EndInvestigatorTurn", false);
        // This will cause the next phase if nothing was added
        game.CurrentQuest.eManager.TriggerEvent();

        // Display the transition dialog for investigator phase
        ChangePhaseWindow.DisplayTransitionWindow(Quest.MoMPhase.mythos);
    }

    // Mark a monster as activated
    override public void MonsterActivated()
    {
        Game game = Game.Get();

        // Check for any partial monster activations
        foreach (Quest.Monster m in game.CurrentQuest.monsters)
        {
            if (m.minionStarted || m.masterStarted)
            {
                m.activated = true;
            }
        }

        // Activate a monster
        if (ActivateMonster())
        {
            CheckNewRound();
        }
    }

    // Activate a monster
    override public bool ActivateMonster()
    {
        Game game = Game.Get();

        // Search for unactivated monsters
        List<int> notActivated = new List<int>();
        // Get the index of all monsters that haven't activated
        for (int i = 0; i < game.CurrentQuest.monsters.Count; i++)
        {
            if (!game.CurrentQuest.monsters[i].activated)
            {
                QuestMonster qm = game.CurrentQuest.monsters[i].monsterData as QuestMonster;
                if (qm != null && qm.activations != null && qm.activations.Length == 1 &&
                    qm.activations[0].IndexOf("Event") == 0
                    && game.CurrentQuest.eManager.events[qm.activations[0]].Disabled())
                {
                    // monster cannot be activated, mark as activated
                    game.CurrentQuest.monsters[i].activated = true;
                }
                else
                {
                    notActivated.Add(i);
                }
            }
        }

        if (notActivated.Count > 0)
        {
            // Find a random unactivated monster
            Quest.Monster toActivate = game.CurrentQuest.monsters[notActivated[Random.Range(0, notActivated.Count)]];

            // Find out of this monster is quest specific
            QuestMonster qm = toActivate.monsterData as QuestMonster;
            if (qm != null && qm.activations != null && qm.activations.Length == 1 &&
                qm.activations[0].IndexOf("Event") == 0)
            {
                toActivate.masterStarted = true;
                toActivate.activated = true;
                game.CurrentQuest.eManager.monsterImage = toActivate;
                game.CurrentQuest.eManager.QueueEvent(qm.activations[0]);
            }
            else
            {
                ActivateMonster(toActivate);
            }

            // Return false as activations remain
            return false;
        }

        return true;
    }

    public override void EndRound()
    {
        endRoundRequested = true;
        base.EndRound();
    }


    // Check if there are events that are required at the end of the round
    public override bool CheckNewRound()
    {
        Game game = Game.Get();

        // Return if there is an event open
        if (game.CurrentQuest.eManager.currentEvent != null)
            return false;

        // Return if there is an event queued
        if (game.CurrentQuest.eManager.eventStack.Count > 0)
            return false;

        if (game.CurrentQuest.phase == Quest.MoMPhase.investigator)
        {
            return false;
        }

        if (game.CurrentQuest.phase == Quest.MoMPhase.mythos)
        {
            if (game.CurrentQuest.monsters.Count > 0)
            {
                if (ActivateMonster())
                {
                    // no monster can be activated (activation conditions may prevent existing monster from doing anything), switch to horror phase
                    game.CurrentQuest.phase = Quest.MoMPhase.horror;
                    game.stageUI.Update();
                    return false;
                }
                // this is a recursive call, so we don't want to bring back monsters, if we have reached the end of activations
                else if (game.CurrentQuest.phase != Quest.MoMPhase.horror)
                {
                    // going through monster activation: switch to phase monsters
                    game.CurrentQuest.phase = Quest.MoMPhase.monsters;
                    game.stageUI.Update();
                    return game.CurrentQuest.eManager.currentEvent != null;
                }
            }
            else
            {
                game.CurrentQuest.phase = Quest.MoMPhase.horror;
                game.stageUI.Update();
                EndRound();
                return game.CurrentQuest.eManager.currentEvent != null;
            }
        }

        if (game.CurrentQuest.phase == Quest.MoMPhase.monsters)
        {
            game.CurrentQuest.phase = Quest.MoMPhase.horror;
            game.stageUI.Update();
            return false;
        }

        // we need this test to make sure user can do the horro test, as a random event would switch the game to investigator phase 
        if (!endRoundRequested && game.CurrentQuest.phase == Quest.MoMPhase.horror &&
            game.CurrentQuest.monsters.Count > 0)
        {
            return false;
        }

        // Finishing the round

        // reset the endRound Request
        endRoundRequested = false;

        // Clear all investigator activated
        foreach (Quest.Hero h in game.CurrentQuest.heroes)
        {
            h.activated = false;
        }

        //  Clear monster activations
        foreach (Quest.Monster m in game.CurrentQuest.monsters)
        {
            m.activated = false;
            m.minionStarted = false;
            m.masterStarted = false;
            m.currentActivation = null;
        }

        // Advance to next round
        int round = Mathf.RoundToInt(game.CurrentQuest.vars.GetValue("#round")) + 1;
        game.CurrentQuest.vars.SetValue("#round", round);
        game.CurrentQuest.log.Add(new Quest.LogEntry(new StringKey("val", "ROUND", round).Translate()));

        game.CurrentQuest.log.Add(new Quest.LogEntry(new StringKey("val", "PHASE_INVESTIGATOR").Translate()));

        game.CurrentQuest.phase = Quest.MoMPhase.investigator;
        game.stageUI.Update();
        game.monsterCanvas.UpdateList();

        game.audioControl.PlayTrait("newround");

        // Start of round events
        if (game.CurrentQuest.vars.GetValue("#eliminatedprev") > 0 &&
            game.CurrentQuest.vars.GetValue("#eliminatedcomplete") <= 0)
        {
            game.CurrentQuest.eManager.EventTriggerType("StartFinalRound");
        }

        game.CurrentQuest.eManager.EventTriggerType("StartRound");

        SaveManager.Save(0);

        // Display the transition dialog for investigator phase
        ChangePhaseWindow.DisplayTransitionWindow(Quest.MoMPhase.investigator);

        return true;
    }
}