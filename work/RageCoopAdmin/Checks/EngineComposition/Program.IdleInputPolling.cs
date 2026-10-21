using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using LemonUI;

internal static partial class Program
{
    private static int _idleControlReads;

    private static void CheckIdleInputPolling()
    {
        Type script = Resource.GetType("RageCoopAdmin.Client.AdminMenuScript", true)!;
        var boundary = new Harmony("friendslegacy.check.idleinput");
        try
        {
            foreach (string name in new[] { "ProcessQuickControls", "ProcessMenus", "SuppressMenuGameActions", "IsInputBlocked", "Pressed", "JustPressed" })
                boundary.Patch(script.GetMethod(name, Instance | Static)!, transpiler: new HarmonyMethod(typeof(Program), nameof(IsolateInputBoundaries)));
            foreach (string name in new[] { "Pressed", "JustPressed" })
                boundary.Patch(script.GetMethod(name, Static)!, prefix: new HarmonyMethod(typeof(Program), nameof(CountIdleControlRead)));
            object instance = FormatterServices.GetUninitializedObject(script);
            foreach (string name in new[] { "_interfaceSettings", "_shortcutLatch", "_gamepadChord", "_cancelReleaseGuard", "_shortcutMenuInputGuard", "_cancelMenuInputGuard", "_textInputActivationGuard" })
            {
                FieldInfo field = script.GetField(name, Instance)!;
                field.SetValue(instance, Activator.CreateInstance(field.FieldType, true));
            }
            var pool = (ObjectPool)FormatterServices.GetUninitializedObject(typeof(ObjectPool));
            typeof(ObjectPool).GetFields(Instance).Single(field => field.FieldType == typeof(List<IProcessable>)).SetValue(pool, new List<IProcessable>());
            script.GetField("_pool", Instance)!.SetValue(instance, pool);
            InputHeld.Clear(); InputEdges.Clear(); InputKeys.Clear();
            _inputChat = _inputHostMenu = _inputPause = false;
            void Frame()
            {
                bool consumed = (bool)script.GetMethod("ProcessQuickControls", Instance)!.Invoke(instance, new object[] { Stopwatch.GetTimestamp() })!;
                script.GetMethod("ProcessMenus", Instance)!.Invoke(instance, new object[] { consumed });
            }
            _idleControlReads = 0;
            for (int frame = 0; frame < 10; frame++) Frame();
            Assert(_idleControlReads == 100,
                "Ten idle controller frames with every menu hidden read only bumper bindings; navigation, accept and cancel do not poll GTA controls.");
            object settings = script.GetField("_interfaceSettings", Instance)!.GetValue(instance)!;
            settings.GetType().GetProperty("GamepadShortcuts")!.SetValue(settings, false);
            _idleControlReads = 0;
            Frame();
            Assert(_idleControlReads == 0, "Disabling gamepad shortcuts leaves no idle control polling in the assembled resource UI.");
            // A hidden root still owns a previously consumed cancel until its
            // release frame. The optimization must not skip that state.
            object release = script.GetField("_cancelReleaseGuard", Instance)!.GetValue(instance)!;
            release.GetType().GetMethod("Update", Instance)!.Invoke(release, new object[] { true, true });
            _idleControlReads = 0;
            InputHeld.Add((2, 202));
            Frame();
            Assert(_idleControlReads > 0 && InputDisabled.Contains((2, 202)),
                "A hidden menu continues consuming the held Xbox cancel it captured before closure.");
            InputHeld.Clear();
            for (int frame = 0; frame < 4; frame++) Frame();
            _idleControlReads = 0;
            Frame();
            Assert(_idleControlReads == 0, "After cancel release the hidden UI returns to zero control polling.");
        }
        finally
        {
            InputHeld.Clear(); InputEdges.Clear(); InputKeys.Clear(); InputDisabled.Clear();
            boundary.UnpatchAll(boundary.Id);
        }
    }

    private static void CountIdleControlRead() => _idleControlReads++;
}
