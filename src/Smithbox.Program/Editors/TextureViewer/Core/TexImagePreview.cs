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
    
    // NOVA: Cache para evitar flickering - armazena a última textura válida por contexto
    private Dictionary<string, (TextureResource resource, SubTexture subTexture)> _lastValidTexture = new();
    
    // NOVA: Cache de texturas base por arquivo para evitar recarregamentos desnecessários
    private Dictionary<string, Dictionary<string, TextureResource>> _baseTextureCache = new();

    public void ClearIcons()
    {
        foreach (var entry in LoadedResources)
        {
            entry.Value.Dispose();
        }

        LoadedResources.Clear();
        
        // NOVA: Limpar também os novos caches
        _lastValidTexture.Clear();
        
        foreach (var fileCache in _baseTextureCache.Values)
        {
            foreach (var texture in fileCache.Values)
            {
                texture.Dispose();
            }
        }
        _baseTextureCache.Clear();
    }

    /// <summary>
    /// Display the Image Preview texture in the Param Editor properties view
    /// </summary>
    public bool DisplayImagePreview(Param.Row context, IconConfig iconConfig, object fieldValue, string fieldName, int columnIndex)
    {
        var resourceKey = $"{fieldName}_{columnIndex}";
        var contextKey = $"{context?.ID}_{fieldName}_{fieldValue}";

        if (Project.ParamData.IconConfigurations == null)
            return false;

        // Check Icon Config, if not present then don't attempt to load or display anything
        if (iconConfig == null)
        {
            // NOVA: Se não há config mas temos uma textura válida anterior, usa ela para evitar flickering
            return TryDisplayLastValidTexture(resourceKey, contextKey);
        }

        if (Project.ParamData.IconConfigurations.Configurations == null)
        {
            return TryDisplayLastValidTexture(resourceKey, contextKey);
        }

        var iconEntry = Project.ParamData.IconConfigurations.Configurations.Where(e => e.Name == iconConfig.TargetConfiguration).FirstOrDefault();

        if (iconEntry == null)
        {
            return TryDisplayLastValidTexture(resourceKey, contextKey);
        }

        // NOVA: Verificar se já temos a textura correta carregada para este contexto específico
        if (LoadedResources.ContainsKey(resourceKey))
        {
            var currentResource = LoadedResources[resourceKey];
            var newSubTexture = GetPreviewSubTexture(currentResource.Name, context, iconConfig, iconEntry, fieldValue, fieldName);
            
            // Se a SubTexture mudou, apenas atualizamos ela sem recriar a TextureResource
            if (newSubTexture != null && (currentResource.SubTexture == null || 
                currentResource.SubTexture.Name != newSubTexture.Name))
            {
                currentResource.SubTexture = newSubTexture;
                _lastValidTexture[contextKey] = (currentResource, newSubTexture);
            }
            
            if (currentResource.GPUTexture != null && CFG.Current.Param_FieldContextMenu_ImagePreview_FieldColumn)
            {
                DisplayImage(currentResource);
                return true;
            }
        }

        // NOVA: Tentar usar cache de textura base primeiro
        var targetFile = Project.TextureData.TextureFiles.Entries.FirstOrDefault(e => e.Filename == iconEntry.File);
        if (targetFile == null)
        {
            return TryDisplayLastValidTexture(resourceKey, contextKey);
        }

        // Se a textura base já está carregada, reutilizar
        if (_baseTextureCache.ContainsKey(iconEntry.File))
        {
            var cachedTextures = _baseTextureCache[iconEntry.File];
            var newSubTexture = GetPreviewSubTexture("", context, iconConfig, iconEntry, fieldValue, fieldName);
            
            if (newSubTexture != null)
            {
                // Procurar pela textura que contém nossa SubTexture
                foreach (var cachedTexture in cachedTextures.Values)
                {
                    if (cachedTexture.Name == newSubTexture.Name.Split('_')[0] || 
                        DoesTextureContainSubTexture(cachedTexture.Name, newSubTexture))
                    {
                        // Criar uma nova instância apenas com SubTexture diferente
                        var resourceCopy = CloneResourceWithNewSubTexture(cachedTexture, newSubTexture);
                        
                        if (LoadedResources.ContainsKey(resourceKey))
                        {
                            LoadedResources[resourceKey].Dispose();
                        }
                        
                        LoadedResources[resourceKey] = resourceCopy;
                        _lastValidTexture[contextKey] = (resourceCopy, newSubTexture);
                        
                        if (CFG.Current.Param_FieldContextMenu_ImagePreview_FieldColumn)
                        {
                            DisplayImage(resourceCopy);
                        }
                        return true;
                    }
                }
            }
        }

        // Se chegou aqui, precisa carregar a textura pela primeira vez
        return LoadNewTexture(resourceKey, contextKey, iconEntry, targetFile, context, iconConfig, fieldValue, fieldName);
    }

    // NOVA: Método para tentar exibir a última textura válida (anti-flickering)
    private bool TryDisplayLastValidTexture(string resourceKey, string contextKey)
    {
        if (_lastValidTexture.ContainsKey(contextKey))
        {
            var (lastResource, lastSubTexture) = _lastValidTexture[contextKey];
            
            if (lastResource?.GPUTexture != null)
            {
                // Usar a última textura válida para evitar flickering
                if (!LoadedResources.ContainsKey(resourceKey))
                {
                    LoadedResources[resourceKey] = lastResource;
                }
                
                if (CFG.Current.Param_FieldContextMenu_ImagePreview_FieldColumn)
                {
                    DisplayImage(lastResource);
                }
                return true;
            }
        }
        
        return false;
    }

    // NOVA: Método para clonar recurso com nova SubTexture
    private TextureResource CloneResourceWithNewSubTexture(TextureResource original, SubTexture newSubTexture)
    {
        var clone = new TextureResource(original.TpfFile, original.TexIndex);
        clone.SubTexture = newSubTexture;
        clone._LoadTexture(AccessLevel.AccessFull);
        return clone;
    }

    // NOVA: Verifica se uma textura contém determinada SubTexture
    private bool DoesTextureContainSubTexture(string textureName, SubTexture subTexture)
    {
        if (Editor.Project.TextureData.PrimaryBank.ShoeboxEntries == null)
            return false;

        var shoeboxEntry = Editor.Project.TextureData.PrimaryBank.ShoeboxEntries.FirstOrDefault();
        if (shoeboxEntry.Value?.Textures == null)
            return false;

        return shoeboxEntry.Value.Textures.ContainsKey(textureName) &&
               shoeboxEntry.Value.Textures[textureName].Any(st => st.Name == subTexture.Name);
    }

    // NOVA: Método para carregar nova textura (refatorado do código original)
    private bool LoadNewTexture(string resourceKey, string contextKey, IconConfigurationEntry iconEntry, 
        TextureFileEntry targetFile, Param.Row context, IconConfig iconConfig, object fieldValue, string fieldName)
    {
        Task<bool> loadTask = Project.TextureData.PrimaryBank.LoadTextureBinder(targetFile);
        Task.WaitAll(loadTask);

        var targetBinder = Project.TextureData.PrimaryBank.Entries.FirstOrDefault(e => e.Key.Filename == targetFile.Filename);
        if (targetBinder.Value == null)
            return false;

        // Inicializar cache da textura base se necessário
        if (!_baseTextureCache.ContainsKey(iconEntry.File))
        {
            _baseTextureCache[iconEntry.File] = new Dictionary<string, TextureResource>();
        }

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

                            // Adicionar ao cache base
                            if (!_baseTextureCache[iconEntry.File].ContainsKey(curTex.Name))
                            {
                                var baseResource = new TextureResource(curTpf, index);
                                baseResource._LoadTexture(AccessLevel.AccessFull);
                                _baseTextureCache[iconEntry.File][curTex.Name] = baseResource;
                            }

                            // Adicionar ao cache de recursos
                            if (LoadedResources.ContainsKey(resourceKey))
                            {
                                LoadedResources[resourceKey].Dispose();
                            }
                            
                            LoadedResources[resourceKey] = newResource;
                            _lastValidTexture[contextKey] = (newResource, curPreviewTexture);

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