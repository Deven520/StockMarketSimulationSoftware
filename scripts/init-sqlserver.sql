-- SQL Server 2012+ 建库脚本（业务表也可由 EnsureCreated / Migrator 生成）
IF DB_ID(N'StockSim') IS NULL
BEGIN
    CREATE DATABASE StockSim;
END
GO
