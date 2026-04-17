using System;
using System.Collections.Generic;
using HarmonyLib;

namespace SEMIDEAD.Patches;

/// <summary>
/// Guards EnemyOnScreen.GetOnScreen against KeyNotFoundException.
/// Wave enemies are spawned outside R.E.P.O.'s normal director flow and may not
/// be registered in EnemyOnScreen's internal dictionary.
/// EnemyThinMan.Update → EnemyOnScreen.GetOnScreen → KeyNotFoundException.
/// The Finalizer sets the return value to false (not on screen) and swallows the exception.
/// </summary>
[HarmonyPatch(typeof(EnemyOnScreen), "GetOnScreen")]
static class EnemyOnScreenPatch
{
    private static Exception? Finalizer(Exception? __exception, ref bool __result)
    {
        if (__exception is KeyNotFoundException)
        {
            __result = false;
            return null; // swallow — enemy treated as off-screen
        }
        return __exception; // re-throw anything else unmodified
    }
}
