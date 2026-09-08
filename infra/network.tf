# One delegated subnet per workload for outbound VNet integration, so routing/NSG changes for one
# can't widen the other's blast radius. Inbound private endpoints stay in the landing zone's
# data.azurerm_subnet.pe - only outbound needs its own subnet, since delegation is exclusive.
#   - Subnets and route-table associations are looped: the entries differ only in name and address
#     space. Add a workload by adding a map entry.
#   - NSGs stay as two explicit resources: their rule sets are expected to diverge (func vs api),
#     which a for_each would make more awkward.

locals {
  workload_subnets = {
    func = "10.243.5.0/24"
    api  = "10.243.6.0/24"
  }
}

resource "azurerm_subnet" "workload" {
  for_each             = local.workload_subnets
  name                 = "con-snet-cap-${each.key}-${local.instance}"
  resource_group_name  = data.azurerm_resource_group.network.name
  virtual_network_name = data.azurerm_virtual_network.main.name
  address_prefixes     = [each.value]

  delegation {
    name = "webapp-delegation"

    service_delegation {
      name    = "Microsoft.Web/serverFarms"
      actions = ["Microsoft.Network/virtualNetworks/subnets/action"]
    }
  }
}

resource "azurerm_network_security_group" "func" {
  name                = "con-nsg-func-cap-${local.env}-${local.region}-${local.instance}"
  location            = var.location
  resource_group_name = data.azurerm_resource_group.network.name
  tags                = local.common_tags

  security_rule {
    name                       = "DenyInternetInbound"
    priority                   = 200
    direction                  = "Inbound"
    access                     = "Deny"
    protocol                   = "*"
    source_port_range          = "*"
    destination_port_range     = "*"
    source_address_prefix      = "Internet"
    destination_address_prefix = "*"
  }
}

resource "azurerm_network_security_group" "api" {
  name                = "con-nsg-api-cap-${local.env}-${local.region}-${local.instance}"
  location            = var.location
  resource_group_name = data.azurerm_resource_group.network.name
  tags                = local.common_tags

  security_rule {
    name                       = "DenyInternetInbound"
    priority                   = 200
    direction                  = "Inbound"
    access                     = "Deny"
    protocol                   = "*"
    source_port_range          = "*"
    destination_port_range     = "*"
    source_address_prefix      = "Internet"
    destination_address_prefix = "*"
  }
}

resource "azurerm_subnet_network_security_group_association" "func" {
  subnet_id                 = azurerm_subnet.workload["func"].id
  network_security_group_id = azurerm_network_security_group.func.id
}

resource "azurerm_subnet_network_security_group_association" "api" {
  subnet_id                 = azurerm_subnet.workload["api"].id
  network_security_group_id = azurerm_network_security_group.api.id
}

resource "azurerm_subnet_route_table_association" "workload" {
  for_each       = local.workload_subnets
  subnet_id      = azurerm_subnet.workload[each.key].id
  route_table_id = data.azurerm_route_table.spoke.id
}
