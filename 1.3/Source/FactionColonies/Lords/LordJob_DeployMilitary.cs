﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FactionColonies.util;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace FactionColonies
{
	class LordJob_DeployMilitary : LordJob
	{
		private MercenarySquadFC squad;
		private IntVec3 currentOrderPosition;
		private int whenToForceLeave;
		private int timeDeployed = 0;
		private DeployedMilitaryCommandMenu deployedMilitaryCommandMenu;
		private MilitaryOrder currentOrder = MilitaryOrder.DefendPoint;

		private LordToil_DefendPoint lordToil_DefendPoint;
		private LordToil_HuntEnemies lordToil_HuntEnemies;

		public LordJob_DeployMilitary()
		{ 
		}

		public LordJob_DeployMilitary(IntVec3 currentOrderPosition, MercenarySquadFC squad, int maxDeploymentTime = 30000)
		{
			this.currentOrderPosition = currentOrderPosition;
			this.squad = squad;

			whenToForceLeave = maxDeploymentTime + Find.TickManager.TicksGame;
			timeDeployed = Find.TickManager.TicksGame;

			Init();
		}

		/// <summary>
		/// Initializes some variables after loading is complete or when the deployment is first started
		/// </summary>
		private void Init()
		{
			deployedMilitaryCommandMenu = new DeployedMilitaryCommandMenu();
			if (!Find.WindowStack.IsOpen(typeof(DeployedMilitaryCommandMenu))) Find.WindowStack.Add(deployedMilitaryCommandMenu);
			else deployedMilitaryCommandMenu = (DeployedMilitaryCommandMenu) Find.WindowStack.Windows.First(window => window.GetType() == typeof(DeployedMilitaryCommandMenu));

			deployedMilitaryCommandMenu.squadMilitaryOrderDic.SetOrAdd(squad, currentOrder);
			deployedMilitaryCommandMenu.currentOrderPositionDic[squad] = currentOrderPosition;

			lordToil_DefendPoint = new LordToil_DefendPoint(currentOrderPosition);
			lordToil_HuntEnemies = new LordToil_HuntEnemies(currentOrderPosition);
		}

		public override void ExposeData()
		{
			base.ExposeData();
			Scribe_Values.Look(ref currentOrderPosition, "currentOrderPosition");
			Scribe_Values.Look(ref timeDeployed, "timeDeployed");
			Scribe_Values.Look(ref whenToForceLeave, "whenToForceLeave");
			Scribe_Values.Look(ref currentOrder, "currentOrder");
			Scribe_References.Look(ref squad, "squad");

			//PostLoadInit is the last loading pass
			if (Scribe.mode == LoadSaveMode.PostLoadInit) Init();
		}

		private void UpdateOrderPosition()
		{
			currentOrderPosition = deployedMilitaryCommandMenu.currentOrderPositionDic[squad];
			lordToil_DefendPoint.SetDefendPoint(deployedMilitaryCommandMenu.currentOrderPositionDic[squad]);
			((LordToilData_HuntEnemies)lordToil_HuntEnemies.data).fallbackLocation = deployedMilitaryCommandMenu.currentOrderPositionDic[squad];
		}

		/// <summary>
		/// Creates a list of transitions to allow for leaving from any state when time runs out
		/// </summary>
		/// <param name="stateGraph"></param>
		/// <returns>the Transitions</returns>
		private IEnumerable<Transition> AnyThingToLeavingTransitions(StateGraph stateGraph) 
		{
			for (int i = 0; i < stateGraph.lordToils.Count - 1; i++)
			{
				yield return new Transition(stateGraph.lordToils[i], stateGraph.lordToils.Last()) 
				{ 
					triggers = new List<Trigger>(1) { new Trigger_Custom((TriggerSignal _) => Find.TickManager.TicksGame > whenToForceLeave) },
					preActions = new List<TransitionAction>(1) 
					{ 
						new TransitionAction_Custom(delegate()
						{
							squad.isDeployed = false;
							Messages.Message("militaryPawnsLeavingTimeOut".Translate(), lord.ownedPawns, MessageTypeDefOf.NeutralEvent);
						}) 
					}
				};
			}
		}

		/// <summary>
		/// This generates all transitions needed for the control window to work
		/// </summary>
		/// <param name="stateGraph"></param>
		/// <returns>the Transitions</returns>
		private IEnumerable<Transition> AnyPlayerChoiceTransition(StateGraph stateGraph)
		{
			for (int i = 0; i < stateGraph.lordToils.Count; i++)
			{
				for (int j = 0; j < stateGraph.lordToils.Count; j++)
				{
					if (i == j) continue;

					//save j as another variable, otherwise j refers to the same number as the one the loop uses, which in the end is always Count
					int k = j;
					yield return new Transition(stateGraph.lordToils[i], stateGraph.lordToils[j])
					{
						triggers = new List<Trigger>(1)
						{
							new Trigger_Custom((TriggerSignal _) => deployedMilitaryCommandMenu.squadMilitaryOrderDic[squad] == (MilitaryOrder)k + 1)
						},
						preActions = new List<TransitionAction>(1)
						{
							new TransitionAction_Custom(UpdateOrderPosition)
						}
					};
				}
			}
		}

		/// <summary>
		/// If the player selects a new position to move to, the currentOrderPosition must be updated, and the jobs restarted.
		/// This <c>Transition</c> ensures that.
		/// </summary>
		/// <param name="stateGraph"></param>
		/// <returns></returns>
		private Transition RefreshMovementTransition(StateGraph stateGraph)
		{
			return new Transition(stateGraph.lordToils[0], stateGraph.lordToils[0], true)
			{
				triggers = new List<Trigger>(1)
				{
					new Trigger_Custom((TriggerSignal _) => currentOrderPosition != deployedMilitaryCommandMenu.currentOrderPositionDic[squad])
				},
				preActions = new List<TransitionAction>(1)
				{
					new TransitionAction_Custom(UpdateOrderPosition)
				}
			};
		}

		/// <summary>
		/// The Order of toils in the StateGraph MUST be the same as in the MilitaryOrder enum
		/// </summary>
		/// <returns>a StateGraph</returns>
		public override StateGraph CreateGraph()
		{
			StateGraph stateGraph = new StateGraph { StartingToil = lordToil_DefendPoint };

			stateGraph.AddToil(lordToil_HuntEnemies);
			stateGraph.AddToil(new LordToil_RecoverWoundedAndLeave(new LordToilData_ExitMap() { canDig = false, locomotion = LocomotionUrgency.Jog, interruptCurrentJob = true }));

			stateGraph.AddTransition(RefreshMovementTransition(stateGraph));
			stateGraph.AddTransitions(AnyPlayerChoiceTransition(stateGraph));
			stateGraph.AddTransitions(AnyThingToLeavingTransitions(stateGraph));

			return stateGraph;
		}

		public override void Notify_LordDestroyed()
		{
			if (squad.getSettlement.militarySquad == squad)
			{
				squad.getSettlement.cooldownMilitary();
			}

			squad.isDeployed = false;
			base.Notify_LordDestroyed();
		}
	}
}