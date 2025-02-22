using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Experimental.Rendering;
using System.Collections.Generic;
using System.Runtime.InteropServices;

#if UNITY_STANDALONE_WIN && UNITY_64
using UnityEngine.NVIDIA;
#endif

#if DLSS_INSTALLED
using NVIDIA = UnityEngine.NVIDIA;
#endif

namespace AEG.DLSS
{
    [RequireComponent(typeof(Camera))]
    public class DLSS_URP : DLSS_UTILS
    {
#if AEG_DLSS && UNITY_STANDALONE_WIN && UNITY_64

        public RTHandleSystem RTHandleS;
        public RTHandle m_dlssOutput;
        public RTHandle m_colorBuffer;
        public Texture m_depthBuffer;
        public Texture m_motionVectorBuffer;
        public GraphicsFormat CameraGraphicsOutput = GraphicsFormat.B10G11R11_UFloatPack32;

        private bool initFirstFrame = false;

        //UniversalRenderPipelineAsset
        private DLSSScriptableRenderFeature dlssScriptableRenderFeature;
        private bool containsRenderFeature = false;
        private UniversalRenderPipelineAsset UniversalRenderPipelineAsset;
        private UniversalAdditionalCameraData m_cameraData;

        public DlssViewData dlssData;
        public ViewState state;


        //Camera Stacking
        public bool m_cameraStacking = false;
        public Camera m_topCamera;
        private int m_prevCameraStackCount;
        private bool m_isBaseCamera;
        private List<DLSS_URP> m_prevCameraStack = new List<DLSS_URP>();
        private NVIDIA.DLSSQuality m_prevStackQuality = (NVIDIA.DLSSQuality)(-1);



        protected override void InitializeDLSS() {
            base.InitializeDLSS();
            m_mainCamera.depthTextureMode = DepthTextureMode.Depth | DepthTextureMode.MotionVectors;

            SetupResolution();

            if(!m_dlssInitialized) {
                RenderPipelineManager.beginCameraRendering += PreRenderCamera;
                RenderPipelineManager.endCameraRendering += PostRenderCamera;
            }

            state = new ViewState(device);

            if(m_cameraData == null) {
                m_cameraData = m_mainCamera.GetUniversalAdditionalCameraData();
                if(m_cameraData != null) {
                    if(m_cameraData.renderType == CameraRenderType.Base) {
                        m_isBaseCamera = true;
                        SetupCameraStacking();
                    }
                }
            }
        }

        /// <summary>
        /// Sets up the buffers, initializes the DLSS context, and sets up the command buffer
        /// Must be recalled whenever the display resolution changes
        /// </summary>
        private void SetupCommandBuffer() {
            if(m_dlssOutput != null) {
                m_dlssOutput.Release();
            }
            if(m_colorBuffer != null) {
                m_colorBuffer.Release();
            }

            if(dlssScriptableRenderFeature != null) {
                dlssScriptableRenderFeature.OnDispose();
            } else {
                containsRenderFeature = GetRenderFeature();
            }

            float _upscaleRatio = GetUpscaleRatioFromQualityMode(DLSSQuality);

            m_renderWidth = (int)(m_displayWidth / _upscaleRatio);
            m_renderHeight = (int)(m_displayHeight / _upscaleRatio);

            m_dlssOutput = RTHandleS.Alloc(m_displayWidth, m_displayHeight, enableRandomWrite: true, colorFormat: CameraGraphicsOutput, msaaSamples: MSAASamples.None, name: "DLSS OUTPUT");

#if !UNITY_2022_1_OR_NEWER
            m_colorBuffer = RTHandleS.Alloc(m_renderWidth, m_renderHeight, enableRandomWrite: false, colorFormat: CameraGraphicsOutput, msaaSamples: MSAASamples.None, name: "DLSS INPUT");
#endif

            dlssData.inputRes = new Resolution() { width = m_renderWidth, height = m_renderHeight };
            dlssData.outputRes = new Resolution() { width = m_displayWidth, height = m_displayHeight };

            SetDynamicResolution(_upscaleRatio);

            if(!containsRenderFeature) {
                Debug.LogError("Current Universal Render Data is missing the 'DLSS Scriptable Render Pass' Rendering Feature");
            } else {
                dlssScriptableRenderFeature.OnSetReference(this);
            }

            dlssScriptableRenderFeature.IsEnabled = true;
        }

        private bool GetRenderFeature() {
            UniversalRenderPipelineAsset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;

            if(UniversalRenderPipelineAsset != null) {
                UniversalRenderPipelineAsset.upscalingFilter = UpscalingFilterSelection.Linear;
                UniversalRenderPipelineAsset.msaaSampleCount = (int)MsaaQuality.Disabled;

                var type = UniversalRenderPipelineAsset.GetType();
                var propertyInfo = type.GetField("m_RendererDataList", BindingFlags.Instance | BindingFlags.NonPublic);

                if(propertyInfo != null) {
                    var scriptableRenderData = (ScriptableRendererData[])propertyInfo.GetValue(UniversalRenderPipelineAsset);

                    if(scriptableRenderData != null && scriptableRenderData.Length > 0) {
                        foreach(var renderData in scriptableRenderData) {

                            foreach(var rendererFeature in renderData.rendererFeatures) {
                                dlssScriptableRenderFeature = rendererFeature as DLSSScriptableRenderFeature;


                                if(dlssScriptableRenderFeature != null) {
                                    return true;
                                    //Todo give error when RenderFeature is disabled
                                }
                            }
                        }
                    }
                }
            } else {
                Debug.LogError("DLSS 2: Can't find UniversalRenderPipelineAsset");
            }
            return false;
        }

        private void PreRenderCamera(ScriptableRenderContext context, Camera cameras) {
            if(cameras != m_mainCamera) {
                return;
            }

            //Check if display resolution has changed
            if(m_displayWidth != Display.main.renderingWidth || m_displayHeight != Display.main.renderingHeight) {
                SetupResolution();
            }

            if(m_previousScaleFactor != m_scaleFactor || m_previousRenderingPath != m_mainCamera.actualRenderingPath || !initFirstFrame) {
                initFirstFrame = true;
                SetupFrameBuffers();
            }

            if(UniversalRenderPipelineAsset != GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset) {
                dlssScriptableRenderFeature.OnDispose();
                dlssScriptableRenderFeature = null;
                OnSetQuality(DLSSQuality);
                SetupCommandBuffer();
            }

            JitterCameraMatrix();
            UpdateDlssSettings(ref dlssData, state, DLSSQuality, device);

            //Camera Stacking
            if(m_isBaseCamera) {
                if(m_cameraData != null) {
                    if(m_cameraStacking) {
                        try {
                            if(m_topCamera != m_cameraData.cameraStack[m_cameraData.cameraStack.Count - 1] || m_prevCameraStackCount != m_cameraData.cameraStack.Count || m_prevStackQuality != DLSSQuality) {
                                SetupCameraStacking();
                            }
                        }
                        catch { }
                    }
                }
            }
        }

        private void PostRenderCamera(ScriptableRenderContext context, Camera cameras) {
            if(cameras != m_mainCamera) {
                return;
            }

            m_mainCamera.ResetProjectionMatrix();
        }

        /// <summary>
        /// DLSS TAA Jitter
        /// </summary>
        private void JitterCameraMatrix() {
            if(dlssScriptableRenderFeature == null) {
                return;
            } else if(!dlssScriptableRenderFeature.IsEnabled) {
                return;
            }

            var m_jitterMatrix = GetJitteredProjectionMatrix(m_mainCamera.projectionMatrix, m_renderWidth, m_renderHeight, m_antiGhosting, m_mainCamera);
            var m_projectionMatrix = m_mainCamera.projectionMatrix;
            m_mainCamera.nonJitteredProjectionMatrix = m_projectionMatrix;
            m_mainCamera.projectionMatrix = m_jitterMatrix;
            m_mainCamera.useJitteredProjectionMatrixForTransparentRendering = true;
        }

        /// <summary>
        /// Handle Dynamic Scaling
        /// </summary>
        /// <param name="_value"></param>
        private void SetDynamicResolution(float _value) {
            if(UniversalRenderPipelineAsset != null) {
                UniversalRenderPipelineAsset.renderScale = (1 / _value);
            }
        }

        /// <summary>
        /// Creates new buffers and sends them to the plugin
        /// </summary>
        private void SetupFrameBuffers() {
            m_previousScaleFactor = m_scaleFactor;

            SetupCommandBuffer();

            m_previousRenderingPath = m_mainCamera.actualRenderingPath;
        }

        /// <summary>
        /// Creates new buffers, sends them to the plugin, and reintilized DLSS to adjust the display size
        /// </summary>
        private void SetupResolution() {
            m_displayWidth = m_mainCamera.pixelWidth;
            m_displayHeight = m_mainCamera.pixelHeight;

            RTHandleS = new RTHandleSystem();
            RTHandleS.Initialize(m_mainCamera.pixelWidth, m_mainCamera.pixelHeight);

            SetupFrameBuffers();
        }

        /// <summary>
        /// Automatically Setup camera stacking
        /// </summary>
        private void SetupCameraStacking() {
            m_prevCameraStackCount = m_cameraData.cameraStack.Count;
            if(m_cameraData.renderType == CameraRenderType.Base) {
                m_isBaseCamera = true;

                m_cameraStacking = m_cameraData.cameraStack.Count > 0;
                if(m_cameraStacking) {
                    CleanupOverlayCameras();
                    m_prevStackQuality = DLSSQuality;

                    m_topCamera = m_cameraData.cameraStack[m_cameraData.cameraStack.Count - 1];

                    for(int i = 0; i < m_cameraData.cameraStack.Count; i++) {
                        DLSS_URP stackedCamera = m_cameraData.cameraStack[i].gameObject.GetComponent<DLSS_URP>();
                        if(stackedCamera == null) {
                            stackedCamera = m_cameraData.cameraStack[i].gameObject.AddComponent<DLSS_URP>();
                        }
                        m_prevCameraStack.Add(m_cameraData.cameraStack[i].gameObject.GetComponent<DLSS_URP>());

                        //stackedCamera.hideFlags = HideFlags.HideInInspector;
                        stackedCamera.m_cameraStacking = true;
                        stackedCamera.m_topCamera = m_topCamera;

                        stackedCamera.OnSetQuality(DLSSQuality);

                        stackedCamera.sharpening = sharpening;
                        stackedCamera.m_antiGhosting = m_antiGhosting;
                    }
                }
            }
        }

        private void CleanupOverlayCameras() {
            for(int i = 0; i < m_prevCameraStack.Count; i++) {
                if(!m_prevCameraStack[i].m_isBaseCamera)
                    DestroyImmediate(m_prevCameraStack[i]);
            }
            m_prevCameraStack = new List<DLSS_URP>();
        }

        protected override void DisableDLSS() {
            base.DisableDLSS();

            RenderPipelineManager.beginCameraRendering -= PreRenderCamera;
            RenderPipelineManager.endCameraRendering -= PostRenderCamera;

            SetDynamicResolution(1);
            if(dlssScriptableRenderFeature != null) {
                dlssScriptableRenderFeature.IsEnabled = false;
            }
            CleanupOverlayCameras();
            m_previousScaleFactor = -1;
            m_prevStackQuality = (NVIDIA.DLSSQuality)(-1);

            if(m_dlssOutput != null) {
                m_dlssOutput.Release();
            }
            if(m_colorBuffer != null) {
                m_colorBuffer.Release();
            }

            //DLSS_UTILS.Cleanup(CommandBuffer test);
        }

#endif
    }
}
