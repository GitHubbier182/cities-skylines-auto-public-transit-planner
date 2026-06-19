using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using ColossalFramework;
using ColossalFramework.Math;
using ColossalFramework.UI;
using ICities;
using UnityEngine;

namespace AutoPublicTransit
{
    public class AutoPublicTransitLoading : LoadingExtensionBase
    {
        private GameObject _go;

        public override void OnLevelLoaded(LoadMode mode)
        {
            base.OnLevelLoaded(mode);

            if (mode != LoadMode.LoadGame && mode != LoadMode.NewGame)
                return;

            State.ClearTransient();

            if (_go == null)
            {
                _go = new GameObject("AutoPublicTransitRoot");
                UnityEngine.Object.DontDestroyOnLoad(_go);

                _go.AddComponent<Manager>();

                var view = UIView.GetAView();
                if (view != null)
                {
                    view.AddUIComponent(typeof(AutoPublicTransitUI));
                    AutoPublicTransitLauncherButton.CreateIfNeeded(view);
                    AutoPublicTransitV2WelcomePanel.ShowIfNeeded(view);
                }
            }
        }

        public override void OnLevelUnloading()
        {
            base.OnLevelUnloading();

            if (_go != null)
            {
                UnityEngine.Object.Destroy(_go);
                _go = null;
            }

            AutoPublicTransitLauncherButton.DestroyInstance();
            AutoPublicTransitV2WelcomePanel.DestroyInstance();
            AutoPublicTransitUI.DestroyInstance();
            State.ClearTransient();
        }
    }
}
