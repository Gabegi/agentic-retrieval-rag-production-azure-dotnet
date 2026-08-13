# ---------------------------------------------------------------------------
# Separate subnets for the compute tier's outbound VNet integration - one per
# workload rather than shared, so routing/NSG changes for one (e.g. the
# Function App's content-share carve-out below) can't accidentally affect the
# other's blast radius. Both still delegate to Microsoft.Web/serverFarms.
# Private endpoints (inbound, and for Search/Storage/Key Vault) stay in the
# existing data.azurerm_subnet.pe - only outbound egress needs its own
# delegated subnet, since delegation is exclusive per subnet.
# ---------------------------------------------------------------------------

# One subnet + NSG + pair of associations per workload, looped via for_each
# rather than copy-pasted per workload - both entries are otherwise identical
# (same delegation, same "deny internet inbound" NSG rule), differing only in
# name and address space. Add a workload by adding a map entry, not by
# copying a block.
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

# Separate NSGs per subnet (rather than one shared NSG) so rule changes for
# one workload can't accidentally affect the other's blast radius. Kept as
# two explicit resources rather than looped - only 2 of them, and unlike the
# subnets above their rule sets are expected to diverge (func vs api may need
# different rules), which a for_each would make more awkward, not less.
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
  subnet_id                  = azurerm_subnet.workload["func"].id
  network_security_group_id  = azurerm_network_security_group.func.id
}

resource "azurerm_subnet_network_security_group_association" "api" {
  subnet_id                  = azurerm_subnet.workload["api"].id
  network_security_group_id  = azurerm_network_security_group.api.id
}

resource "azurerm_subnet_route_table_association" "workload" {
  for_each       = local.workload_subnets
  subnet_id      = azurerm_subnet.workload[each.key].id
  route_table_id = data.azurerm_route_table.spoke.id
}
