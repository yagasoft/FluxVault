-- Prepared input only. Execute solely through the approved finite SYSTEM worker.
-- psql -X -w -v ON_ERROR_STOP=1; no connection pool or automatic retry.
DO $check$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_database WHERE datname = 'fluxvault_single')
       OR EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'fluxvault_service') THEN
        RAISE EXCEPTION 'FluxVault target database/account already exists; refusing adoption';
    END IF;
END
$check$;

CREATE ROLE fluxvault_service LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS;
COMMENT ON ROLE fluxvault_service IS 'FluxVault installation 7871ff7f8d1b404db20771f2e742364f';
CREATE DATABASE fluxvault_single OWNER fluxvault_service;
REVOKE ALL ON DATABASE fluxvault_single FROM PUBLIC;
COMMENT ON DATABASE fluxvault_single IS 'FluxVault installation 7871ff7f8d1b404db20771f2e742364f';

SELECT json_build_object('database', datname, 'owner', pg_get_userbyid(datdba))
FROM pg_database WHERE datname = 'fluxvault_single';
