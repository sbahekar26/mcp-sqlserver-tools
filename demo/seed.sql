-- Demo database so the server can be run and reviewed with no SQL Server instance.
-- Build it with:  sqlite3 demo/dealership.db < demo/seed.sql

DROP TABLE IF EXISTS ServiceOrders;
DROP TABLE IF EXISTS Vehicles;
DROP TABLE IF EXISTS Dealerships;

CREATE TABLE Dealerships (
    Id       INTEGER PRIMARY KEY,
    Name     TEXT    NOT NULL,
    Province TEXT    NOT NULL,
    Oem      TEXT    NOT NULL
);

CREATE TABLE Vehicles (
    Vin          TEXT    PRIMARY KEY,
    DealershipId INTEGER NOT NULL REFERENCES Dealerships(Id),
    Model        TEXT    NOT NULL,
    ModelYear    INTEGER NOT NULL
);

CREATE TABLE ServiceOrders (
    Id        INTEGER PRIMARY KEY,
    Vin       TEXT    NOT NULL REFERENCES Vehicles(Vin),
    OpenedOn  TEXT    NOT NULL,
    ClosedOn  TEXT,
    LabourHrs REAL    NOT NULL,
    Status    TEXT    NOT NULL
);

INSERT INTO Dealerships VALUES
 (1, 'Lakeshore Motors',   'ON', 'Nissan'),
 (2, 'Bow Valley Auto',    'AB', 'Infiniti'),
 (3, 'Rive-Sud Automobile','QC', 'Mitsubishi');

INSERT INTO Vehicles VALUES
 ('1N4AL3AP1JC100001', 1, 'Altima',  2024),
 ('1N4AL3AP1JC100002', 1, 'Rogue',   2025),
 ('JN1EV7AR5MM100003', 2, 'Q50',     2023),
 ('JA4J4UA82PZ100004', 3, 'Outlander', 2025);

INSERT INTO ServiceOrders VALUES
 (1, '1N4AL3AP1JC100001', '2026-06-02', '2026-06-02', 1.5, 'Closed'),
 (2, '1N4AL3AP1JC100002', '2026-06-11', NULL,         3.0, 'Open'),
 (3, 'JN1EV7AR5MM100003', '2026-06-14', '2026-06-16', 6.25,'Closed'),
 (4, 'JA4J4UA82PZ100004', '2026-07-01', NULL,         0.5, 'Waiting Parts'),
 (5, '1N4AL3AP1JC100001', '2026-07-19', NULL,         2.0, 'Open');
