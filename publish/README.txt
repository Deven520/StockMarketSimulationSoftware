StockSim local publish
======================
Web:    D:\Projects\StockMarketWeb\StockMarketSimulationSoftware\publish\web
Worker: D:\Projects\StockMarketWeb\StockMarketSimulationSoftware\publish\worker
SQLite: D:\Projects\StockMarketWeb\StockMarketSimulationSoftware\publish\web\stocksim.db
Replay: D:\Projects\StockMarketWeb\StockMarketSimulationSoftware\data\replay
AI:     D:\Projects\StockMarketWeb\StockMarketSimulationSoftware\data\ai
URL:    http://localhost:5167

Start:  .\scripts\start-local.ps1
Stop:   .\scripts\stop-local.ps1

DB: on full republish choose Keep(K) or Reset(R); or -KeepDatabase / -ResetDatabase.
Alerts: copied from src\StockSim.Web\appsettings.json into publish\web and publish\worker.
Site is open (no login).