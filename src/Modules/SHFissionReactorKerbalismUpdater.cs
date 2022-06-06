using System.Collections.Generic;
using System.Linq;
using KSP.Localization;
using KERBALISM;
using SystemHeat;

namespace KerbalismSystemHeat
{
	public class SystemHeatFissionReactorKerbalismUpdater : PartModule
	{
		public static string brokerName = "SHFissionReactor";
		public static string brokerTitle = Localizer.Format("#LOC_KerbalismSystemHeat_Brokers_FissionReactor");

		[KSPField(isPersistant = true)]
		public bool FirstLoad = true;

		// This should correspond to the related ModuleSystemHeatFissionReactor
		[KSPField(isPersistant = true)]
		public string reactorModuleID;

		[KSPField(isPersistant = true)]
		public float MaxECGeneration = 0f;
		[KSPField(isPersistant = true)]
		public float MinThrottle = 0.25f;
		[KSPField(isPersistant = true)]
		public float MaxThrottle = 1.0f;

		protected static string reactorModuleName = "ModuleSystemHeatFissionReactor";
		protected ModuleSystemHeatFissionReactor reactorModule;

		protected bool resourcesListParsed = false;
		protected List<ResourceRatio> inputs;
		protected List<ResourceRatio> outputs;

		public virtual void Start()
		{
			if (Lib.IsFlight() || Lib.IsEditor())
			{
				if (reactorModule == null)
				{
					reactorModule = FindReactorModule(part, reactorModuleID);
				}
				if (FirstLoad)
				{
					if (reactorModule != null)
					{
						MaxECGeneration = reactorModule.ElectricalGeneration.Evaluate(100f);
						MinThrottle = reactorModule.MinimumThrottle / 100f;
					}
					if (inputs == null || inputs.Count == 0)
					{
						ConfigNode node = ModuleUtils.GetModuleConfigNode(part, moduleName);
						if (node != null)
						{
							OnLoad(node);
						}
					}
					FirstLoad = false;
				}
			}
		}

		public override void OnLoad(ConfigNode node)
		{
			base.OnLoad(node);
		}

		public virtual void FixedUpdate()
		{
			if (reactorModule != null && Lib.IsFlight())
			{
				// Update MaxThrottle according to reactor CoreIntegrity
				MaxThrottle = reactorModule.CoreIntegrity / 100f;
				if (MinThrottle > MaxThrottle)
				{
					MinThrottle = MaxThrottle;
				}
			}
		}

		// Fetch input/output resources list from reactor ConfigNode
		protected void ParseResourcesList(Part part)
		{
			if (!resourcesListParsed)
			{
				ConfigNode node = ModuleUtils.GetModuleConfigNode(part, reactorModuleName);
				if (node != null)
				{
					// Load resource nodes
					ConfigNode[] inNodes = node.GetNodes("INPUT_RESOURCE");
					inputs = new List<ResourceRatio>();
					for (int i = 0; i < inNodes.Length; i++)
					{
						ResourceRatio p = new ResourceRatio();
						p.Load(inNodes[i]);
						inputs.Add(p);
					}
					ConfigNode[] outNodes = node.GetNodes("OUTPUT_RESOURCE");
					outputs = new List<ResourceRatio>();
					for (int i = 0; i < outNodes.Length; i++)
					{
						ResourceRatio p = new ResourceRatio();
						p.Load(outNodes[i]);
						outputs.Add(p);
					}
				}
				resourcesListParsed = true;
			}
		}

		// Estimate resources production/consumption for Kerbalism planner
		// This will be called by Kerbalism in the editor (VAB/SPH), possibly several times after a change to the vessel
		public string PlannerUpdate(List<KeyValuePair<string, double>> resourceChangeRequest, CelestialBody body, Dictionary<string, double> environment)
		{
			if (reactorModule != null)
			{
				float curECGeneration = reactorModule.ElectricalGeneration.Evaluate(reactorModule.CurrentReactorThrottle);
				if (curECGeneration > 0)
				{
					resourceChangeRequest.Add(new KeyValuePair<string, double>("ElectricCharge", curECGeneration));
				}

				float fuelThrottle = reactorModule.CurrentReactorThrottle / 100f;
				if (fuelThrottle > 0)
				{
					ParseResourcesList(part);
					foreach (ResourceRatio ratio in inputs)
					{
						resourceChangeRequest.Add(new KeyValuePair<string, double>(ratio.ResourceName, -fuelThrottle * ratio.Ratio));
					}
					foreach (ResourceRatio ratio in outputs)
					{
						resourceChangeRequest.Add(new KeyValuePair<string, double>(ratio.ResourceName, fuelThrottle * ratio.Ratio));
					}
				}
				return brokerTitle;
			}
			return "ERR: no reactor";
		}

		// Simulate resources production/consumption for unloaded vessel
		public static string BackgroundUpdate(Vessel v, ProtoPartSnapshot part_snapshot, ProtoPartModuleSnapshot module_snapshot, PartModule proto_part_module, Part proto_part, Dictionary<string, double> availableResources, List<KeyValuePair<string, double>> resourceChangeRequest, double elapsed_s)
		{
			ProtoPartModuleSnapshot reactor = KSHUtils.FindPartModuleSnapshot(part_snapshot, reactorModuleName);
			if (reactor != null)
			{
				if (Lib.Proto.GetBool(reactor, "Enabled"))
				{
					float curThrottle = Lib.Proto.GetFloat(reactor, "CurrentReactorThrottle") / 100f;
					float minThrottle = Lib.Proto.GetFloat(module_snapshot, "MinThrottle");
					float maxThrottle = Lib.Proto.GetFloat(module_snapshot, "MaxThrottle");
					float maxECGeneration = Lib.Proto.GetFloat(module_snapshot, "MaxECGeneration");
					bool needToStopReactor = false;
					if (maxECGeneration > 0)
					{
						VesselResources resources = KERBALISM.ResourceCache.Get(v);
						if (!(proto_part_module as SystemHeatFissionReactorKerbalismUpdater).resourcesListParsed)
						{
							(proto_part_module as SystemHeatFissionReactorKerbalismUpdater).ParseResourcesList(proto_part);
						}

						// Mininum reactor throttle
						// Some input/output resources will always be consumed/produced as long as minThrottle > 0
						if (minThrottle > 0)
						{
							ResourceRecipe recipe = new ResourceRecipe(KERBALISM.ResourceBroker.GetOrCreate(
								brokerName,
								KERBALISM.ResourceBroker.BrokerCategory.Converter,
								brokerTitle));
							foreach (ResourceRatio ir in (proto_part_module as SystemHeatFissionReactorKerbalismUpdater).inputs)
							{
								recipe.AddInput(ir.ResourceName, ir.Ratio * minThrottle * elapsed_s);
								if (resources.GetResource(v, ir.ResourceName).Amount < double.Epsilon)
								{
									// Input resource amount is zero - stop reactor
									needToStopReactor = true;
								}
							}
							foreach (ResourceRatio or in (proto_part_module as SystemHeatFissionReactorKerbalismUpdater).outputs)
							{
								recipe.AddOutput(or.ResourceName, or.Ratio * minThrottle * elapsed_s, dump: false);
								if (1 - resources.GetResource(v, or.ResourceName).Level < double.Epsilon)
								{
									// Output resource is at full capacity
									needToStopReactor = true;
									Message.Post(
										Severity.warning,
										Localizer.Format(
											"#LOC_KerbalismSystemHeat_ReactorOutputResourceFull",
											or.ResourceName,
											v.GetDisplayName(),
											part_snapshot.partName)
									);
								}
							}
							recipe.AddOutput("ElectricCharge", minThrottle * maxECGeneration * elapsed_s, dump: true);
							resources.AddRecipe(recipe);
						}

						if (!needToStopReactor)
						{
							if (!Lib.Proto.GetBool(reactor, "ManualControl"))
							{
								// Automatic reactor throttle mode
								curThrottle = maxThrottle;
							}
							curThrottle -= minThrottle;
							if (curThrottle > 0)
							{
								ResourceRecipe recipe = new ResourceRecipe(KERBALISM.ResourceBroker.GetOrCreate(
									brokerName,
									KERBALISM.ResourceBroker.BrokerCategory.Converter,
									brokerTitle));
								foreach (ResourceRatio ir in (proto_part_module as SystemHeatFissionReactorKerbalismUpdater).inputs)
								{
									recipe.AddInput(ir.ResourceName, ir.Ratio * curThrottle * elapsed_s);
									if (resources.GetResource(v, ir.ResourceName).Amount < double.Epsilon)
									{
										// Input resource amount is zero - stop reactor
										needToStopReactor = true;
									}
								}
								foreach (ResourceRatio or in (proto_part_module as SystemHeatFissionReactorKerbalismUpdater).outputs)
								{
									recipe.AddOutput(or.ResourceName, or.Ratio * curThrottle * elapsed_s, dump: false);
									if (1 - resources.GetResource(v, or.ResourceName).Level < double.Epsilon)
									{
										// Output resource is at full capacity
										needToStopReactor = true;
										Message.Post(
											Severity.warning,
											Localizer.Format(
												"#LOC_KerbalismSystemHeat_ReactorOutputResourceFull",
												or.ResourceName,
												v.GetDisplayName(),
												part_snapshot.partName)
										);
									}
								}
								recipe.AddOutput("ElectricCharge", curThrottle * maxECGeneration * elapsed_s, dump: false);
								resources.AddRecipe(recipe);
							}
						}
					}
					// Disable reactor
					if (needToStopReactor)
					{
						Lib.Proto.Set(reactor, "Enabled", false);
					}
				}
				// Prevent resource consumption in ModuleSystemHeatFissionReactor.DoCatchup()
				// by setting LastUpdate to current time
				Lib.Proto.Set(reactor, "LastUpdateTime", Planetarium.GetUniversalTime());
				return brokerTitle;
			}
			return "ERR: no reactor";
		}

		// Find associated Reactor module
		public ModuleSystemHeatFissionReactor FindReactorModule(Part part, string moduleName)
		{
			ModuleSystemHeatFissionReactor reactor = part.GetComponents<ModuleSystemHeatFissionReactor>().ToList().Find(x => x.moduleID == moduleName);

			if (reactor == null)
			{
				KSHUtils.LogError($"[{part}] No ModuleSystemHeatFissionReactor named {moduleName} was found, using first instance.");
				reactorModule = part.GetComponents<ModuleSystemHeatFissionReactor>().ToList().First();
			}
			if (reactor == null)
			{
				KSHUtils.LogError($"[{part}] No ModuleSystemHeatFissionReactor was found.");
			}
			return reactor;
		}
	}
}
