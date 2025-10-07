using BepInEx;
using HarmonyLib;
using HutongGames.PlayMaker;
using System.Collections;
using System.Linq;
using TeamCherry.Localization;
using UnityEngine;
using HutongGames.PlayMaker.Actions;

namespace Silksong.Mods.SistersOfSin
{
    class Sinner
    {
        private const int SINGLE_SINNER_HP = 1300;
        public const int DEATH_HP_THRESHOLD = 100;
        public readonly GameObject gameObject;
        public readonly PlayMakerFSM[] allFSMs;
        public readonly PlayMakerFSM controlFSM;
        public readonly HealthManager healthManager;
        
        public Sinner(string tag)
        {
            gameObject = GameObject.Find($"Boss Scene{tag}")?.transform.Find("First Weaver")?.gameObject;
            if (gameObject != null)
            {
                allFSMs = gameObject.GetComponents<PlayMakerFSM>();
                controlFSM = allFSMs.FirstOrDefault(f => f.FsmName == "Control");
                healthManager = gameObject.GetComponent<HealthManager>();
                healthManager.hp = SINGLE_SINNER_HP;
                Object.Destroy(gameObject.LocateMyFSM("Stun Control"));
                
                //Increase healing time, feedback from LeonHK
                var bindState = controlFSM.FsmStates.FirstOrDefault(state => state.Name == "Bind Silk");
                if (bindState != null) {
                    foreach (var action in bindState.Actions) {
                        if (action is Wait wait) {
                            wait.time = 2.2f;
                            break;
                        }
                    }
                }
            }
        }
    }
	[BepInPlugin("com.astrumnova.sistersofsin", "Sisters Of Sin", "0.0.1")]
	[BepInProcess("Hollow Knight Silksong.exe")]
	public class SistersOfSin : BaseUnityPlugin
	{
        private static Sinner sinnerA;
        private static Sinner sinnerB;
        private static Sinner sinnerC;
        private static bool phase2Mode;
        private const string P2_TRIGGER_STATE = "P2 Tele Pause";
        
		private void Awake()
		{
			Harmony.CreateAndPatchAll(typeof(SistersOfSin));
			Logger.LogInfo("com.astrumnova.sistersofsin loaded and initialized!");
            
            StartCoroutine(WaitAndPatch()); //Change name from First Sinner to Sisters Of Sin
		}
        [HarmonyPostfix]
        [HarmonyPatch(typeof(PlayMakerFSM), "OnEnable")]
        private static void OnFsmEnabled(PlayMakerFSM __instance)
        {
            if (__instance.gameObject.scene.name == "Slab_10b" && __instance.name == "Boss Scene" && !__instance.name.Contains("CLONE"))
            {
                //Main cloning logic, snippet copied from eniac-mod's SkongBoss Doubler
                GameObject sinnerSceneB = Instantiate(__instance.gameObject, __instance.transform.position, __instance.transform.rotation, __instance.transform.parent);
                GameObject sinnerSceneC = Instantiate(__instance.gameObject, __instance.transform.position, __instance.transform.rotation, __instance.transform.parent);
                __instance.name += "CLONEA";
                sinnerSceneB.name += "CLONEB";
                sinnerSceneC.name += "CLONEC";
                
                //This already happens when hornet dies but people may restart the bossfight through the GODHOME mod, this is to account for that
                sinnerA = null;
                sinnerB = null;
                sinnerC = null;
                phase2Mode = false;
            }
        }
        [HarmonyPostfix]
        [HarmonyPatch(typeof(FsmState), "OnEnter")]
        private static void OnFSMStateEntered(FsmState __instance)
        {
            var fsm = __instance.Fsm?.FsmComponent;
            if (fsm == null) { return; }
            if (fsm.name == "First Weaver" && fsm.gameObject.scene.name == "Slab_10b")
            {
                if (__instance.Name == "First Idle")
                {
                    if (sinnerA == null && sinnerB == null && sinnerC == null)
                    {
                        sinnerA = new Sinner("CLONEA");
                        sinnerB = new Sinner("(Clone)CLONEB");
                        sinnerC = new Sinner("(Clone)CLONEC");
                        
                        //Make them teleport away instantly so you dont get free hits at the start,
                        //this used to be an iframe system but i changed my mind because it didnt feel good
                        sinnerA.controlFSM.SetState("Cancel To Tele");
                        sinnerB.controlFSM.SetState("Cancel To Tele");
                        sinnerC.controlFSM.SetState("Cancel To Tele");
                    }
                }
                if (__instance.Name == "Death Stagger F")
                {
                    //Set hp to 750 if one goes down in phase 1, should be around 200 hp healing on average
                    sinnerA.healthManager.hp = 750;
                    sinnerB.healthManager.hp = 750;
                    sinnerC.healthManager.hp = 750;
                }
                if (__instance.Name == P2_TRIGGER_STATE && !phase2Mode)
                {
                    HandlePhase2(sinnerA);
                    HandlePhase2(sinnerB);
                    HandlePhase2(sinnerC);
                    
                    //If all 3 sinners are paused in the P2_TRIGGER_STATE it means theyre all ready for phase 2
                    if (sinnerA.controlFSM.ActiveStateName == P2_TRIGGER_STATE &&
                        sinnerB.controlFSM.ActiveStateName == P2_TRIGGER_STATE &&
                        sinnerC.controlFSM.ActiveStateName == P2_TRIGGER_STATE)
                    {
                        sinnerA.controlFSM.Fsm.ManualUpdate = false;
                        sinnerB.controlFSM.Fsm.ManualUpdate = false;
                        sinnerC.controlFSM.Fsm.ManualUpdate = false;
                        
                        sinnerA.healthManager.hp = 750;
                        sinnerB.healthManager.hp = 750;
                        sinnerC.healthManager.hp = 750;
                        
                        phase2Mode = true;
                    }
                }
                if (__instance.Name == "Hornet Dead")
                {
                    sinnerA = null;
                    sinnerB = null;
                    sinnerC = null;
                    phase2Mode = false;
                }
            }
        }
        //If a sinner is in the state im using to sync up all 3 for the second phase, pause the fsm
        private static void HandlePhase2(Sinner sinner)
        {
            if (sinner.controlFSM.ActiveStateName == P2_TRIGGER_STATE && !sinner.controlFSM.Fsm.ManualUpdate)
            {
                sinner.controlFSM.Fsm.ManualUpdate = true;
            }
        }
        
        //Cooldown system for the flat damage boost, basically since the nail damage is split in 3 when averaging out the shared hp
        //the sinners can easily out heal your damage, so i implemented a system where roughly every 6/10ths of a second your next 
        //damage instance will have a damage boost, its on a cooldown to balance fast damaging things like some tools and threadstorm
        //while still letting most needle hits benefit from the boost, bit of a hacky system for sure but it works
        private static int cooldown;
		private void FixedUpdate()
		{
            if (sinnerA != null && sinnerB != null && sinnerC != null)
            {
                //Average out 3 health managers
                if (sinnerA.healthManager.hp != sinnerB.healthManager.hp ||
                    sinnerB.healthManager.hp != sinnerC.healthManager.hp ||
                    sinnerC.healthManager.hp != sinnerA.healthManager.hp)
                {
                    var average = (sinnerA.healthManager.hp + sinnerB.healthManager.hp + sinnerC.healthManager.hp - (cooldown == 0 ? 30 : 0)) / 3;
                    sinnerA.healthManager.hp = sinnerB.healthManager.hp = sinnerC.healthManager.hp = average;
                    if (cooldown == 0) cooldown = 30;
                }
                if (sinnerA.healthManager.hp <= Sinner.DEATH_HP_THRESHOLD)
                {
                    sinnerA.healthManager.Die(1, AttackTypes.Generic, false);
                    sinnerB.healthManager.Die(1, AttackTypes.Generic, false);
                    sinnerC.healthManager.Die(1, AttackTypes.Generic, false);
                }
                if (cooldown > 0) cooldown--;
            }
		}
        private IEnumerator WaitAndPatch()
        {
            yield return new WaitForSeconds(2f); // Give game time to init Language
            Harmony.CreateAndPatchAll(typeof(Language_Get_Patch));
        }
	}
    [HarmonyPatch(typeof(Language), "Get")]
    [HarmonyPatch(new [] { typeof(string), typeof(string) })]
    public static class Language_Get_Patch
    {
        private static void Postfix(string key, string sheetTitle, ref string __result)
        {
            if (key == "FIRST_WEAVER_SUPER") __result = "Sisters Of";
            if (key == "FIRST_WEAVER_MAIN") __result = "Sin";
        }
    }
}
