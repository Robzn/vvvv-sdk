﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.ComponentModel.Composition.Primitives;
using System.ComponentModel.Composition.ReflectionModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

using VVVV.Core;
using VVVV.Core.Logging;
using VVVV.Core.Model;
using VVVV.Core.Runtime;
using VVVV.Hosting;
using VVVV.PluginInterfaces.V1;
using VVVV.PluginInterfaces.V2;

namespace VVVV.Hosting.Factories
{
	/// <summary>
	/// DotNetPluginFactory for V1 and V2 style plugins.
	/// V1 style plugins need to be loaded manually
	/// V2 style plugins are loaded via MEF
	/// </summary>
	[Export(typeof(IAddonFactory))]
	[Export(typeof(DotNetPluginFactory))]
	public class DotNetPluginFactory : AbstractFileFactory<IInternalPluginHost>
	{
		class PluginImporter
		{
			[ImportMany(typeof(IPluginBase), AllowRecomposition=true)]
			public List<ExportFactory<IPluginBase, INodeInfoStuff>> NodeInfoExports { get; set; }
		}
		
		[Import]
		protected IHDEHost FHost;
		
		[Import]
		protected ISolution FSolution;
		
		private PluginImporter FPluginImporter = new PluginImporter();
		private Dictionary<INodeInfo, ExportFactory<IPluginBase, INodeInfoStuff>> FMEFPlugins;
		private Dictionary<IPluginBase, ExportLifetimeContext<IPluginBase>> FPluginLifetimeContexts;
		private Dictionary<string, ComposablePartCatalog> FCatalogCache;
		protected Regex FDynamicRegExp = new Regex(@"(.*)\._dynamic_\.[0-9]+\.dll$");
		
		protected HostExportProvider FHostExportProvider;
		public ExportProvider[] ExportProviders
		{
			get;
			private set;
		}
		
		#region Constructor
		[ImportingConstructor]
		public DotNetPluginFactory(CompositionContainer parentContainer)
			: this(parentContainer, ".dll")
		{

		}
		
		protected DotNetPluginFactory(CompositionContainer parentContainer, string fileExtension)
			: base(fileExtension)
		{
			FMEFPlugins = new Dictionary<INodeInfo, ExportFactory<IPluginBase, INodeInfoStuff>>();
			FPluginLifetimeContexts = new Dictionary<IPluginBase, ExportLifetimeContext<IPluginBase>>();
			FCatalogCache = new Dictionary<string, ComposablePartCatalog>();
			FHostExportProvider = new HostExportProvider();
			ExportProviders = new ExportProvider[] { FHostExportProvider, parentContainer };
		}
		#endregion
		
		#region IAddonFactory
		
		public override string JobStdSubPath {
			get {
				return "plugins";
			}
		}
		
		protected override void AddSubDir(string dir, bool recursive)
		{
			// Ignore obj directories used by C# IDEs
			if (dir.EndsWith(@"\obj\x86") || dir.EndsWith(@"\obj\x64"))
				return;
			
			base.AddSubDir(dir, recursive);
		}
		
		protected override bool CreateNode(INodeInfo nodeInfo, IInternalPluginHost pluginHost)
		{
			//dispose previous plugin
			var plugin = pluginHost.Plugin;
			if (plugin != null) DisposePlugin(plugin);
			
			//make the host mark all its pins for possible deletion
			pluginHost.Plugin = null;
			
			//create the plugin
			plugin = CreatePlugin(nodeInfo, pluginHost as IPluginHost2);
			
			var pluginV1 = plugin as IPlugin;
			if (pluginV1 != null)
				pluginV1.SetPluginHost(pluginHost);
			
			pluginHost.Plugin = plugin;
			
			return true;
		}
		
		protected override bool DeleteNode(INodeInfo nodeInfo, IInternalPluginHost pluginHost)
		{
			var plugin = pluginHost.Plugin;
			
			if (plugin != null)
			{
				DisposePlugin(plugin);
				
				return true;
			}
			
			return false;
		}
		
		/// <summary>
		/// Called by AbstractFileFactory to extract all node infos in given file.
		/// </summary>
		protected override IEnumerable<INodeInfo> LoadNodeInfos(string filename)
		{
			var nodeInfos = new List<INodeInfo>();
			
			// We can't handle dynamic plugins
			if (!IsDynamicAssembly(filename))
				LoadNodeInfosFromFile(filename, filename, ref nodeInfos, true);
			
			return nodeInfos;
		}
		
		protected void LoadNodeInfosFromFile(string filename, string sourcefilename, ref List<INodeInfo> nodeInfos, bool commitUpdates)
		{
			// See if it's a .net assembly
			if (!IsDotNetAssembly(filename)) return;
			
			try
			{
				// Check for V2 style plugins
				if (!FCatalogCache.ContainsKey(filename))
					FCatalogCache[filename] = new AssemblyCatalog(filename);
				
				foreach (var nodeInfo in ExtractNodeInfosFromCatalog(FCatalogCache[filename], filename, sourcefilename))
				{
					nodeInfo.Type = NodeType.Plugin;
					nodeInfos.Add(nodeInfo);
				}
				
				if (nodeInfos.Count == 0)
				{
					var assembly = Assembly.LoadFrom(filename);
					
					// Check for V1 style plugins
					foreach (var nodeInfo in ExtractNodeInfosFromAssembly(assembly, sourcefilename))
					{
						nodeInfo.Type = NodeType.Plugin;
						nodeInfos.Add(nodeInfo);
					}
				}
			}
			catch (ReflectionTypeLoadException e)
			{
				FLogger.Log(LogType.Error, "Extracting node infos from {0} caused the following exception:", filename);
				foreach (var f in e.LoaderExceptions)
					FLogger.Log(f);
			}
			catch (Exception e)
			{
				FLogger.Log(LogType.Error, "Extracting node infos from {0} caused the following exception:", filename);
				FLogger.Log(e);
			}
			finally
			{
				foreach (var nodeInfo in nodeInfos)
				{
					nodeInfo.Factory = this;
					if (commitUpdates)
						nodeInfo.CommitUpdate();
				}
			}
		}
		
		#endregion
		
		public IPluginBase CreatePlugin(INodeInfo nodeInfo, IPluginHost2 pluginHost)
		{
			//V2 plugin
			if (FMEFPlugins.ContainsKey(nodeInfo))
			{
				FHostExportProvider.PluginHost = pluginHost;
				
				var lifetimeContext = FMEFPlugins[nodeInfo].CreateExport();
				var plugin = lifetimeContext.Value;
				
				FPluginLifetimeContexts[plugin] = lifetimeContext;
				
				return plugin;
			}
			//V1 plugin
			else
			{
				var assembly = Assembly.LoadFrom(nodeInfo.Filename);
				var plugin = (IPluginBase) assembly.CreateInstance(nodeInfo.Arguments);
				
				return plugin;
			}
			
			throw new InvalidOperationException(string.Format("Can't create plugin '{0}'.", nodeInfo.Systemname));
		}
		
		public void DisposePlugin(IPluginBase plugin)
		{
			if (FPluginLifetimeContexts.ContainsKey(plugin))
			{
				FPluginLifetimeContexts[plugin].Dispose();
				FPluginLifetimeContexts.Remove(plugin);
			}
			else if (plugin is IDisposable)
			{
				((IDisposable) plugin).Dispose();
			}
		}
		
		#region Helper functions
		
		protected IEnumerable<INodeInfo> ExtractNodeInfosFromCatalog(ComposablePartCatalog catalog, string filename, string sourcefilename)
		{
			var nodeInfos = new Dictionary<string, INodeInfo>();
			
			var container = new CompositionContainer(catalog, ExportProviders);
			container.ComposeParts(FPluginImporter);
			
			foreach (var pluginExport in FPluginImporter.NodeInfoExports)
			{
				var metadata = pluginExport.Metadata;
				var nodeInfo = FNodeInfoFactory.CreateNodeInfo(
					metadata.Name,
					metadata.Category,
					metadata.Version,
					sourcefilename,
					true);
				
				nodeInfo.Shortcut = metadata.Shortcut;
				nodeInfo.Author = metadata.Author;
				nodeInfo.Help = metadata.Help;
				nodeInfo.Tags = metadata.Tags;
				nodeInfo.Bugs = metadata.Bugs;
				nodeInfo.Credits = metadata.Credits;
				nodeInfo.Warnings = metadata.Warnings;
				nodeInfo.InitialWindowSize = new Size(metadata.InitialWindowWidth, metadata.InitialWindowHeight);
				nodeInfo.InitialBoxSize = new Size(metadata.InitialBoxWidth, metadata.InitialBoxHeight);
				nodeInfo.InitialComponentMode = metadata.InitialComponentMode;
				nodeInfo.AutoEvaluate = metadata.AutoEvaluate;
				nodeInfo.Ignore = metadata.Ignore;
				
				FMEFPlugins[nodeInfo] = pluginExport;
				nodeInfos.Add(nodeInfo.Systemname, nodeInfo);
			}
			
			if (nodeInfos.Count > 0)
			{
				// Set Arguments property on each INodeInfo.
				foreach (var part in catalog.Parts)
				{
					var lazyPartType = ReflectionModelServices.GetPartType(part);
					
					foreach (var exportDefinition in part.ExportDefinitions)
					{
						var lazyMemberInfo = ReflectionModelServices.GetExportingMember(exportDefinition);
						if (lazyMemberInfo.MemberType == MemberTypes.TypeInfo)
						{
							var exportedPluginTypes = lazyMemberInfo.GetAccessors()
								.Where(memberInfo => typeof(IPluginBase).IsAssignableFrom(memberInfo as Type))
								.Select(memberInfo => memberInfo as Type);
							foreach (var exportedPluginType in exportedPluginTypes)
							{
								var arguments = exportedPluginType.FullName;
								
								var pluginInfoAttribute = Attribute.GetCustomAttribute(exportedPluginType, typeof(PluginInfoAttribute)) as PluginInfoAttribute;
								if (pluginInfoAttribute != null)
									nodeInfos[pluginInfoAttribute.Systemname].Arguments = arguments;
							}
						}
					}
				}
			}
			
			return nodeInfos.Values;
		}
		
		protected IEnumerable<INodeInfo> ExtractNodeInfosFromAssembly(Assembly assembly, string sourcefilename)
		{
			foreach (Type type in assembly.GetTypes())
			{
				// if type implements IPluginBase
				if (!type.IsAbstract && typeof(IPluginBase).IsAssignableFrom(type))
				{
					foreach (PropertyInfo info in type.GetProperties(BindingFlags.Public | BindingFlags.Static))
					{
						if (info.PropertyType == typeof(INodeInfo))
						{
							var pluginNodeInfo = (INodeInfo) info.GetValue(null, null);
							
							var nodeInfo = FNodeInfoFactory.CreateNodeInfo(
								pluginNodeInfo.Name,
								pluginNodeInfo.Category,
								pluginNodeInfo.Version,
								sourcefilename,
								true);
							
							nodeInfo.UpdateFromNodeInfo(pluginNodeInfo);
							nodeInfo.Arguments = type.Namespace + "." + type.Name;
							
							yield return nodeInfo;
							break;
						}
						// The old interface
						else if (info.PropertyType == typeof(IPluginInfo))
						{
							var pluginInfo = (IPluginInfo) info.GetValue(null, null);
							
							var nodeInfo = FNodeInfoFactory.CreateNodeInfo(
								pluginInfo.Name,
								pluginInfo.Category,
								pluginInfo.Version,
								sourcefilename,
								true);
							
							nodeInfo.UpdateFromPluginInfo(pluginInfo);
							nodeInfo.Arguments = type.Namespace + "." + type.Name;
							
							yield return nodeInfo;
						}
					}
				}
			}
		}
		
		// From http://www.anastasiosyal.com/archive/2007/04/17/3.aspx
		private bool IsDotNetAssembly(string fileName)
		{
			using (FileStream fs = new FileStream(fileName, FileMode.Open, FileAccess.Read))
			{
				try
				{
					using (BinaryReader binReader = new BinaryReader(fs))
					{
						try
						{
							fs.Position = 0x3C; //PE Header start offset
							uint headerOffset = binReader.ReadUInt32();

							fs.Position = headerOffset + 0x18;
							UInt16 magicNumber = binReader.ReadUInt16();

							int dictionaryOffset;
							switch (magicNumber)
							{
									case 0x010B: dictionaryOffset = 0x60; break;
									case 0x020B: dictionaryOffset = 0x70; break;
								default:
									throw new Exception("Invalid Image Format");
							}

							//position to RVA 15
							fs.Position = headerOffset + 0x18 + dictionaryOffset + 0x70;


							//Read the value
							uint rva15value = binReader.ReadUInt32();
							return rva15value != 0;
						}
						finally
						{
							binReader.Close();
						}
					}
				}
				finally
				{
					fs.Close();
				}

			}
		}
		
		private bool IsDynamicAssembly(string filename)
		{
			return FDynamicRegExp.IsMatch(filename);
		}
		#endregion
	}

	public interface INodeInfoStuff
	{
		string Name { get; }
		string Category { get; }
		string Version { get; }
		string Shortcut { get; }
		string Author { get; }
		string Help { get; }
		string Tags { get; }
		string Bugs { get; }
		string Credits { get; }
		string Warnings { get; }
		int InitialWindowWidth { get; }
		int InitialWindowHeight { get; }
		int InitialBoxWidth { get; }
		int InitialBoxHeight { get; }
		TComponentMode InitialComponentMode { get; }
		bool AutoEvaluate { get; }
		bool Ignore { get; }
	}
}