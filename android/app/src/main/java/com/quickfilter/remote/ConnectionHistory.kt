package com.quickfilter.remote

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject

/** 成功连接过的电脑历史（最多 5 条），用于快速重连。 */
class ConnectionHistory(private val context: Context) {

    data class Entry(val ip: String, val port: Int, val machine: String, val pin: String)

    private val prefs = context.getSharedPreferences("qf_conn_history", Context.MODE_PRIVATE)

    fun load(): MutableList<Entry> {
        val json = prefs.getString("list", "[]") ?: "[]"
        val arr = JSONArray(json)
        val list = mutableListOf<Entry>()
        for (i in 0 until arr.length()) {
            val o = arr.optJSONObject(i) ?: continue
            list.add(Entry(o.optString("ip"), o.optInt("port", 47900), o.optString("machine", "电脑"), o.optString("pin", "")))
        }
        return list
    }

    fun save(entry: Entry) {
        val list = load()
            .filter { !(it.ip == entry.ip && it.port == entry.port) }
            .toMutableList()
        list.add(0, entry)
        while (list.size > 5) list.removeAt(list.size - 1)
        write(list)
    }

    private fun write(list: List<Entry>) {
        val arr = JSONArray()
        list.forEach { e ->
            arr.put(JSONObject()
                .put("ip", e.ip)
                .put("port", e.port)
                .put("machine", e.machine)
                .put("pin", e.pin))
        }
        prefs.edit().putString("list", arr.toString()).apply()
    }
}
