using Andre.Formats;
using CsvHelper.Configuration.Attributes;
using DotNext.Collections.Generic;
using Hexa.NET.ImGui;
using HexaGen.Runtime;
using SoulsFormats;
using StudioCore.Configuration;
using StudioCore.Core;
using StudioCore.Editors.ParamEditor.META;
using StudioCore.Formats.JSON;
using StudioCore.Resource;
using StudioCore.Resource.Types;
using StudioCore.TextureViewer;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace StudioCore.Editors.TextureViewer;

/// <summary>
/// Dedicated class for the Image Preview, copies parts of the Texture Viewer but decouples it from the interactive aspects.
/// </summary>
public class TexImagePreview : IResourceEventListener
{
    public TextureViewerScreen Editor;
    public ProjectEntry Project;

    private Task LoadingTask;

    public TexImagePreview(TextureViewerScreen editor, ProjectEntry project)
    {
        Editor = editor;
        Project = project;
    }

    private Dictionary<string, TextureResource> LoadedResources = new();
    
    // NOVA: Cache persistente para evitar flickering - a key é baseada no valor do campo
    private Dictionary<string, TextureResource> _stableCache = new();
    
    // NOVA: Cache do último valor válido para cada contexto
    private Dictionary<string, (object lastValue, TextureResource resource)> _valueCache = new();
    
    // NOVA: Contador de frames para evitar múltiplas operações no mesmo frame
    private Dictionary<string, int> _lastUpdateFrame = new();
    private int _currentFrame = 0;

    public void ClearIcons()
    {
        foreach (var entry in LoadedResources)
        {
            entry.Value.Dispose();
        }
        LoadedResources.Clear();
        
        // NOVA: Limpar caches
        foreach (var entry in _stableCache)
        {
            entry.Value?.Dispose();
        }
        _stableCache.Clear();
        
        foreach (var entry in _valueCache)
        {
            entry.Value.resource?.Dispose();
        }
        _valueCache.Clear();
        
        _lastUpdateFrame.Clear();
        _currentFrame = 0;
    }

    /// <summary>
    /// Display the Image Preview texture in the Param Editor properties view
    /// </summary>
    public bool DisplayImagePreview(Param.Row context, IconConfig iconConfig, object fieldValue, string fieldName, int columnIndex)
    {
        _currentFrame++; // Incrementar contador de frames
        
        var resourceKey = $"{fieldName}_{columnIndex}";
        var contextKey = $"{context?.ID}_{fieldName}";
        var stableCacheKey = $"{contextKey}_{fieldValue}";

        // NOVA: Se já renderizamos neste frame, usa cache
        if (_lastUpdateFrame.ContainsKey(contextKey) && _lastUpdateFrame[contextKey] == _currentFrame)
        {
            return TryDisplayFromCache(resourceKey, contextKey, stableCacheKey);
        }
        
        _lastUpdateFrame[contextKey] = _currentFrame;

        // NOVA: Verificar se o valor mudou
        bool valueChanged = true;
        if (_valueCache.ContainsKey(contextKey))
        {
            var (lastValue, _) = _valueCache[contextKey];
            valueChanged = !Equals(lastValue, fieldValue);
        }

        // Se valor não mudou e temos recurso válido, usar cache
        if (!valueChanged && _stableCache.ContainsKey(stableCacheKey))
        {
            var cachedResource = _stableCache[stableCacheKey];
            if (cachedResource?.GPUTexture != null)
            {
                LoadedResources[resourceKey] = cachedResource;
                if (CFG.Current.Param_FieldContextMenu_ImagePreview_FieldColumn)
                {
                    DisplayImage(cachedResource);
                }
                return true;
            }
        }

        // Verificações básicas
        if (Project.ParamData.IconConfigurations?.Configurations == null || iconConfig == null)
        {
            return TryDisplayFromCache(resourceKey, contextKey, stableCacheKey);
        }

        var iconEntry = Project.ParamData.IconConfigurations.Configurations
            .FirstOrDefault(e => e.Name == iconConfig.TargetConfiguration);

        if (iconEntry == null)
        {
            return TryDisplayFromCache(resourceKey, contextKey, stableCacheKey);
        }

        // NOVA: Só recarregar se realmente necessário
        if (valueChanged || !_stableCache.ContainsKey(stableCacheKey))
        {
            var success = LoadNewTexture(resourceKey, stableCacheKey, iconEntry, context, iconConfig, fieldValue, fieldName);
            
            if (success)
            {
                // Atualizar cache de valores
                _valueCache[contextKey] = (fieldValue, _stableCache[stableCacheKey]);
            }
            
            return success;
        }

        // Se chegou aqui, tenta usar o que já tem
        return TryDisplayFromCache(resourceKey, contextKey, stableCacheKey);
    }

    // NOVA: Método para tentar exibir do cache
    private bool TryDisplayFromCache(string resourceKey, string contextKey, string stableCacheKey)
    {
        // Primeiro tenta cache estável
        if (_stableCache.ContainsKey(stableCacheKey))
        {
            var resource = _stableCache[stableCacheKey];
            if (resource?.GPUTexture != null)
            {
                LoadedResources[resourceKey] = resource;
                if (CFG.Current.Param_FieldContextMenu_ImagePreview_FieldColumn)
                {
                    DisplayImage(resource);
                }
                return true;
            }
        }

        // Depois tenta cache de valores
        if (_valueCache.ContainsKey(contextKey))
        {
            var (_, resource) = _valueCache[contextKey];
            if (resource?.GPUTexture != null)
            {
                LoadedResources[resourceKey] = resource;
                if (CFG.Current.Param_FieldContextMenu_ImagePreview_FieldColumn)
                {
                    DisplayImage(resource);
                }
                return true;
            }
        }

        // Por último, tenta LoadedResources
        if (LoadedResources.ContainsKey(resourceKey))
        {
            var resource = LoadedResources[resourceKey];
            if (resource?.GPUTexture != null)
            {
                if (CFG.Current.Param_FieldContextMenu_ImagePreview_FieldColumn)
                {
                    DisplayImage(resource);
                }
                return true;
            }
        }

        return false;
    }

    // NOVA: Método para carregar nova textura (refatorado)
    private bool LoadNewTexture(string resourceKey, string stableCacheKey, IconConfigurationEntry iconEntry, 
        Param.Row context, IconConfig iconConfig, object fieldValue, string fieldName)
    {
        var targetFile = Project.TextureData.TextureFiles.Entries.FirstOrDefault(e => e.Filename == iconEntry.File);

        if (targetFile == null)
            return false;

        try
        {
            Task<bool> loadTask = Project.TextureData.PrimaryBank.LoadTextureBinder(targetFile);
            Task.WaitAll(loadTask);

            var targetBinder = Project.TextureData.PrimaryBank.Entries.FirstOrDefault(e => e.Key.Filename == targetFile.Filename);
            if (targetBinder.Value == null)
                return false;

            int index = 0;
            SubTexture curPreviewTexture = null;
            TextureResource newResource = null;

            // TPF
            foreach (var entry in targetBinder.Value.Files)
            {
                var curTpf = entry.Value;
                index = 0;

                foreach (var curTex in curTpf.Textures)
                {
                    foreach (var curInternalFilename in iconEntry.InternalFiles)
                    {
                        if (curTex.Name == curInternalFilename)
                        {
                            curPreviewTexture = GetPreviewSubTexture(curTex.Name, context, 
                                iconConfig, iconEntry, fieldValue, fieldName);

                            if (curPreviewTexture != null)
                            {
                                newResource = new TextureResource(curTpf, index);
                                newResource.SubTexture = curPreviewTexture;
                                newResource._LoadTexture(AccessLevel.AccessFull);

                                // NOVA: Dispose do recurso anterior no cache estável
                                if (_stableCache.ContainsKey(stableCacheKey))
                                {
                                    _stableCache[stableCacheKey]?.Dispose();
                                }

                                // NOVA: Armazenar em cache estável
                                _stableCache[stableCacheKey] = newResource;
                                LoadedResources[resourceKey] = newResource;

                                if (CFG.Current.Param_FieldContextMenu_ImagePreview_FieldColumn)
                                {
                                    DisplayImage(newResource);
                                }
                                
                                return true;
                            }
                        }
                    }
                    index++;
                }
            }
        }
        catch
        {
            // Em caso de erro, tenta usar cache
            return TryDisplayFromCache(resourceKey, "", stableCacheKey);
        }

        return false;
    }

    public void DisplayImage(TextureResource curResource)
    {
        // Get scaled image size vector
        var scale = CFG.Current.Param_FieldContextMenu_ImagePreviewScale;

        // Get crop bounds
        float Xmin = float.Parse(curResource.SubTexture.X);
        float Xmax = Xmin + float.Parse(curResource.SubTexture.Width);
        float Ymin = float.Parse(curResource.SubTexture.Y);
        float Ymax = Ymin + float.Parse(curResource.SubTexture.Height);

        // Image size should be based on cropped image
        Vector2 size = new Vector2(Xmax - Xmin, Ymax - Ymin) * scale;

        // Get UV coordinates based on full image
        float left = (Xmin) / curResource.GPUTexture.Width;
        float top = (Ymin) / curResource.GPUTexture.Height;
        float right = (Xmax) / curResource.GPUTexture.Width;
        float bottom = (Ymax) / curResource.GPUTexture.Height;

        // Build UV coordinates
        var UV0 = new Vector2(left, top);
        var UV1 = new Vector2(right, bottom);

        if(curResource.GPUTexture != null)
        {
            var textureId = new ImTextureID(curResource.GPUTexture.TexHandle);
            ImGui.Image(textureId, size, UV0, UV1);
        }
    }

    /// <summary>
    /// Get the image preview sub texture
    /// </summary>
    private SubTexture GetPreviewSubTexture(string textureKey, Param.Row context, IconConfig fieldIcon, IconConfigurationEntry iconEntry, object fieldValue, string fieldName)
    {
        // Guard clauses checking the validity of the TextureRef
        if (context[fieldName] == null)
        {
            return null;
        }

        var imageIdx = $"{fieldValue}";

        SubTexture subTex = null;

        // Special-case handling for some icons in AC6 that are awkward to fit into the Icon Configuration system
        if(iconEntry.SubTexturePrefix == "AC6-Weapon" || iconEntry.SubTexturePrefix == "AC6-Armor")
        {
            if(iconEntry.SubTexturePrefix == "AC6-Weapon")
            {
                subTex = GetMatchingSubTexture(textureKey, imageIdx, "WP_A_");

                // If failed, check for WP_R_ match
                if (subTex == null)
                {
                    subTex = GetMatchingSubTexture(textureKey, imageIdx, "WP_R_");
                }

                // If failed, check for WP_L_ match
                if (subTex == null)
                {
                    subTex = GetMatchingSubTexture(textureKey, imageIdx, "WP_L_");
                }
            }

            if (iconEntry.SubTexturePrefix == "AC6-Armor")
            {
                var prefix = "";

                var headEquip = context["headEquip"].Value.Value.ToString();
                var bodyEquip = context["bodyEquip"].Value.Value.ToString();
                var armEquip = context["armEquip"].Value.Value.ToString();
                var legEquip = context["legEquip"].Value.Value.ToString();

                if (headEquip == "1")
                {
                    prefix = "HD_M_";
                }
                if (bodyEquip == "1")
                {
                    prefix = "BD_M_";
                }
                if (armEquip == "1")
                {
                    prefix = "AM_M_";
                }
                if (legEquip == "1")
                {
                    prefix = "LG_M_";
                }

                subTex = GetMatchingSubTexture(textureKey, imageIdx, prefix);
            }
        }
        else
        {
            subTex = GetMatchingSubTexture(textureKey, imageIdx, iconEntry.SubTexturePrefix);
        }

        return subTex;
    }

    public SubTexture GetMatchingSubTexture(string currentTextureName, string imageIndex, string namePrepend)
    {
        if (Editor.Project.TextureData.ShoeboxFiles == null)
            return null;

        if (Editor.Project.TextureData.ShoeboxFiles.Entries == null)
            return null;

        var shoeboxEntry = Editor.Project.TextureData.PrimaryBank.ShoeboxEntries.FirstOrDefault();

        if(shoeboxEntry.Value == null)
            return null;

        if (shoeboxEntry.Value.Textures.ContainsKey(currentTextureName))
        {
            var subTexs = shoeboxEntry.Value.Textures[currentTextureName];

            int matchId;
            var successMatch = int.TryParse(imageIndex, out matchId);

            foreach (var entry in subTexs)
            {
                var SubTexName = entry.Name.Replace(".png", "");

                Match contents = Regex.Match(SubTexName, $@"{namePrepend}([0-9]+)");
                if (contents.Success)
                {
                    var id = contents.Groups[1].Value;

                    int numId;
                    var successNum = int.TryParse(id, out numId);

                    if (successMatch && successNum && matchId == numId)
                    {
                        return entry;
                    }
                }
            }
        }

        return null;
    }

    public void OnResourceLoaded(IResourceHandle handle, int tag)
    {
        // Nothing
    }

    public void OnResourceUnloaded(IResourceHandle handle, int tag)
    {
        // Nothing
    }
}