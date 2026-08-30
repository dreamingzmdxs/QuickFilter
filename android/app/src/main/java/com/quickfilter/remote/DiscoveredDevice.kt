package com.quickfilter.remote

/** 发现的局域网内 QuickFilter 电脑。 */
data class DiscoveredDevice(val ip: String, val port: Int, val machine: String) {
    override fun toString(): String = "$machine（$ip:$port）"
}
