// ----------------------------------------------------------------------------------
//
// Copyright Microsoft Corporation
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// http://www.apache.org/licenses/LICENSE-2.0
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
// ----------------------------------------------------------------------------------

using Microsoft.Azure.Commands.Common.Strategies;
using Microsoft.Azure.Commands.Compute.Automation.Models;
using Microsoft.Azure.Commands.Compute.Properties;
using Microsoft.Azure.Commands.Compute.Strategies;
using Microsoft.Azure.Commands.Compute.Strategies.ComputeRp;
using Microsoft.Azure.Commands.Compute.Strategies.Network;
using Microsoft.Azure.Commands.Compute.Strategies.ResourceManager;
using Microsoft.Azure.Commands.ResourceManager.Common.ArgumentCompleters;
using Microsoft.Azure.Management.Compute.Models;
using Microsoft.Azure.Management.Internal.Network.Version2017_10_01.Models;
using System.Collections.Generic;
using System.Linq;
using System.Management.Automation;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Azure.Commands.Compute.Automation
{
    public partial class NewAzureRmVmss : ComputeAutomationBaseCmdlet
    {
        // SimpleParameterSet
        [Parameter(ParameterSetName = SimpleParameterSet, Mandatory = false)]
        [PSArgumentCompleter(
            "CentOS",
            "CoreOS",
            "Debian",
            "openSUSE-Leap",
            "RHEL",
            "SLES",
            "UbuntuLTS",
            "Win2016Datacenter",
            "Win2012R2Datacenter",
            "Win2012Datacenter",
            "Win2008R2SP1")]
        public string ImageName { get; set; } = "Win2016Datacenter";

        [Parameter(ParameterSetName = SimpleParameterSet, Mandatory = true)]
        public PSCredential Credential { get; set; }

        [Parameter(ParameterSetName = SimpleParameterSet, Mandatory = false)]
        public int InstanceCount { get; set; } = 2;

        [Parameter(ParameterSetName = SimpleParameterSet, Mandatory = false)]
        public string VirtualNetworkName { get; set; }

        [Parameter(ParameterSetName = SimpleParameterSet, Mandatory = false)]
        public string SubnetName { get; set; }

        [Parameter(ParameterSetName = SimpleParameterSet, Mandatory = false)]
        public string PublicIpAddressName { get; set; }

        [Parameter(ParameterSetName = SimpleParameterSet, Mandatory = false)]
        public string DomainNameLabel { get; set; }

        [Parameter(ParameterSetName = SimpleParameterSet, Mandatory = false)]
        public string SecurityGroupName { get; set; }

        [Parameter(ParameterSetName = SimpleParameterSet, Mandatory = false)]
        public string LoadBalancerName { get; set; }

        [Parameter(ParameterSetName = SimpleParameterSet, Mandatory = false)]
        public int[] BackendPort { get; set; } = new[] { 80 };

        [Parameter(ParameterSetName = SimpleParameterSet, Mandatory = false)]
        [LocationCompleter("Microsoft.Compute/virtualMachineScaleSets")]
        public string Location { get; set; }

        // this corresponds to VmSku in the Azure CLI
        [Parameter(ParameterSetName = SimpleParameterSet, Mandatory = false)]
        public string VmSize { get; set; } = "Standard_DS1_v2";

        [Parameter(ParameterSetName = SimpleParameterSet, Mandatory = false)]
        public UpgradeMode UpgradePolicyMode { get; set; }

        [Parameter(ParameterSetName = SimpleParameterSet, Mandatory = false)]
        [ValidateSet("Static", "Dynamic")]
        public string AllocationMethod { get; set; } = "Static";

        [Parameter(ParameterSetName = SimpleParameterSet, Mandatory = false)]
        public string VnetAddressPrefix { get; set; } = "192.168.0.0/16";

        [Parameter(ParameterSetName = SimpleParameterSet, Mandatory = false)]
        public string SubnetAddressPrefix { get; set; } = "192.168.1.0/24";

        [Parameter(ParameterSetName = SimpleParameterSet, Mandatory = false)]
        public string FrontendPoolName { get; set; }

        [Parameter(ParameterSetName = SimpleParameterSet, Mandatory = false)]
        public string BackendPoolName { get; set; }

        [Parameter(
            ParameterSetName = SimpleParameterSet,
            Mandatory = false,
            HelpMessage = "A list of availability zones denoting the IP allocated for the resource needs to come from.",
            ValueFromPipelineByPropertyName = true)]
        public List<string> Zone { get; set; }

        [Parameter(ParameterSetName = SimpleParameterSet, Mandatory = false)]
        public int[] NatBackendPort { get; set; }

        const int FirstPortRangeStart = 50000;

        ResourceConfig<VirtualMachineScaleSet> CreateVmssConfig(
            ImageAndOsType imageAndOsType)
        {
            var resourceGroup = ResourceGroupStrategy.CreateResourceGroupConfig(ResourceGroupName);

            var publicIpAddress = resourceGroup.CreatePublicIPAddressConfig(
                name: PublicIpAddressName,
                domainNameLabel: DomainNameLabel,
                allocationMethod: AllocationMethod);

            var virtualNetwork = resourceGroup.CreateVirtualNetworkConfig(
                name: VirtualNetworkName, addressPrefix: VnetAddressPrefix);

            var subnet = virtualNetwork.CreateSubnet(SubnetName, SubnetAddressPrefix);

            var loadBalancer = resourceGroup.CreateLoadBalancerConfig(
                name: LoadBalancerName);

            var frontendIpConfiguration = loadBalancer.CreateFrontendIPConfiguration(
                name: FrontendPoolName,
                zones: Zone,
                publicIpAddress: publicIpAddress);

            var backendAddressPool = loadBalancer.CreateBackendAddressPool(
                name: BackendPoolName);

            if (BackendPort != null)
            {
                var LoadBalancingRuleName = LoadBalancerName;
                foreach (var backendPort in BackendPort)
                {
                    loadBalancer.CreateLoadBalancingRule(
                        name: LoadBalancingRuleName + backendPort.ToString(),
                        fronendIpConfiguration: frontendIpConfiguration,
                        backendAddressPool: backendAddressPool,
                        frontendPort: backendPort,
                        backendPort: backendPort);
                }
            }

            NatBackendPort = imageAndOsType.UpdatePorts(NatBackendPort);

            var inboundNatPoolName = VMScaleSetName;
            var PortRangeSize = InstanceCount * 2;

            var inboundNatPools = NatBackendPort
                ?.Select((port, i) => 
                {
                    var portRangeStart = FirstPortRangeStart + i * 2000;
                    return loadBalancer.CreateInboundNatPool(
                        name: inboundNatPoolName + port.ToString(),
                        frontendIpConfiguration: frontendIpConfiguration,
                        frontendPortRangeStart: portRangeStart,
                        frontendPortRangeEnd: portRangeStart + PortRangeSize,
                        backendPort: port);
                })
                .ToList();

            return resourceGroup.CreateVirtualMachineScaleSetConfig(
                name: VMScaleSetName,
                subnet: subnet,
                frontendIpConfigurations: new[] { frontendIpConfiguration },
                backendAdressPool: backendAddressPool,
                inboundNatPools: inboundNatPools,
                imageAndOsType: imageAndOsType,
                adminUsername: Credential.UserName,
                adminPassword: new NetworkCredential(string.Empty, Credential.Password).Password,
                vmSize: VmSize,
                instanceCount: InstanceCount,
                upgradeMode: MyInvocation.BoundParameters.ContainsKey(nameof(UpgradePolicyMode))
                    ? UpgradePolicyMode
                    : (UpgradeMode?)null);
        }

        async Task SimpleParameterSetExecuteCmdlet(IAsyncCmdlet asyncCmdlet)
        {
            ResourceGroupName = ResourceGroupName ?? VMScaleSetName;
            VirtualNetworkName = VirtualNetworkName ?? VMScaleSetName;
            SubnetName = SubnetName ?? VMScaleSetName;
            PublicIpAddressName = PublicIpAddressName ?? VMScaleSetName;
            SecurityGroupName = SecurityGroupName ?? VMScaleSetName;
            LoadBalancerName = LoadBalancerName ?? VMScaleSetName;
            FrontendPoolName = FrontendPoolName ?? VMScaleSetName;
            BackendPoolName = BackendPoolName ?? VMScaleSetName;

            ImageAndOsType imageAndOsType = null;

            var vmss = CreateVmssConfig(imageAndOsType);

            var client = new Client(DefaultProfile.DefaultContext);

            // get current Azure state
            var current = await vmss.GetStateAsync(client, new CancellationToken());

            Location = current.UpdateLocation(Location, vmss);

            imageAndOsType = await client.UpdateImageAndOsTypeAsync(
                imageAndOsType, ImageName, Location);

            // generate a domain name label if it's not specified.
            DomainNameLabel = await PublicIPAddressStrategy.UpdateDomainNameLabelAsync(
                domainNameLabel: DomainNameLabel,
                name: VMScaleSetName,
                location: Location,
                client: client);

            var fqdn = PublicIPAddressStrategy.Fqdn(DomainNameLabel, Location);

            vmss = CreateVmssConfig(imageAndOsType);

            var engine = new SdkEngine(client.SubscriptionId);
            var target = vmss.GetTargetState(current, engine, Location);

            var newState = await vmss.UpdateStateAsync(
                client,
                target,
                new CancellationToken(),
                new ShouldProcess(asyncCmdlet),
                asyncCmdlet.ReportTaskProgress);

            var result = newState.Get(vmss);
            if (result == null)
            {
                result = current.Get(vmss);
            }

            if (result != null)
            {
                var psObject = new PSVirtualMachineScaleSet();
                ComputeAutomationAutoMapperProfile.Mapper.Map(result, psObject);
                psObject.FullyQualifiedDomainName = fqdn;

                var port = "<port>";
                var connectionString = imageAndOsType.GetConnectionString(
                    fqdn,
                    Credential.UserName,
                    port);
                var range =
                    FirstPortRangeStart.ToString() +
                    ".." +
                    (FirstPortRangeStart + InstanceCount * 2).ToString();

                asyncCmdlet.WriteVerbose(
                    Resources.VmssUseConnectionString,
                    connectionString);
                asyncCmdlet.WriteVerbose(
                    Resources.VmssPortRange,
                    port, 
                    range);
                asyncCmdlet.WriteObject(psObject);
            }
        }
    }
}
