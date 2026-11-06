\set ON_ERROR_STOP on
-- Alta y corrección de clientes (0009). Datos ficticios.
CREATE FUNCTION pg_temp.uid(t text) RETURNS uuid LANGUAGE sql IMMUTABLE AS $$ SELECT md5('cli'||t)::uuid $$;
CREATE TEMP TABLE comprobacion_clientes(nombre text);
GRANT INSERT, SELECT ON comprobacion_clientes TO manifiestos_app;
CREATE TEMP TABLE ids(clave text PRIMARY KEY, valor uuid);
GRANT INSERT, SELECT ON ids TO manifiestos_app;
CREATE FUNCTION pg_temp.afirmar(ok boolean,n text) RETURNS void LANGUAGE plpgsql AS $$
BEGIN IF ok IS DISTINCT FROM true THEN RAISE EXCEPTION 'FALLO: %',n; END IF; INSERT INTO comprobacion_clientes VALUES(n); END $$;
CREATE FUNCTION pg_temp.rechaza(q text,estado text,n text) RETURNS void LANGUAGE plpgsql AS $$
DECLARE obtenido text;
BEGIN
    BEGIN EXECUTE q; EXCEPTION WHEN OTHERS THEN GET STACKED DIAGNOSTICS obtenido=RETURNED_SQLSTATE; END;
    PERFORM pg_temp.afirmar(obtenido=estado,n||' ['||coalesce(obtenido,'sin error')||']');
END $$;
CREATE FUNCTION pg_temp.contexto(acceso text) RETURNS void LANGUAGE sql AS $$
    SELECT set_config('app.organizacion_id',pg_temp.uid('o')::text,true), set_config('app.acceso_id',pg_temp.uid(acceso)::text,true)
$$;

BEGIN;
INSERT INTO negocio.organizacion VALUES (pg_temp.uid('o'),'Grupo Ficticio Clientes');
INSERT INTO negocio.empresa VALUES (pg_temp.uid('o'),pg_temp.uid('e'),'EMP-C');
INSERT INTO negocio.sucursal(organizacion_id,id,empresa_id,codigo,nombre) VALUES
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('e'),'C1','Sucursal ficticia C1'),
 (pg_temp.uid('o'),pg_temp.uid('s2'),pg_temp.uid('e'),'C2','Sucursal ficticia C2');
INSERT INTO seguridad.acceso_autorizado(organizacion_id,id,correo_permitido,activo,rol_codigo) VALUES
 (pg_temp.uid('o'),pg_temp.uid('esc'),'escribe@example.invalid',true,'operaciones'),
 (pg_temp.uid('o'),pg_temp.uid('lee'),'lee@example.invalid',true,'operaciones');
INSERT INTO seguridad.permiso_sucursal VALUES
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('esc'),'datos.leer'),
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('esc'),'datos.escribir'),
 (pg_temp.uid('o'),pg_temp.uid('s'),pg_temp.uid('lee'),'datos.leer');
COMMIT;

BEGIN;
SET LOCAL ROLE manifiestos_app;
SELECT pg_temp.contexto('esc');
INSERT INTO ids SELECT 'aut', negocio.alta_cliente(pg_temp.uid('o'),pg_temp.uid('s'),'Ferrocarril Ficticio S.A.',NULL,NULL,
  'Carretera Ficticia km 5',NULL,'Centro','66050','El Carmen','Nuevo León',NULL,NULL,' FI-999 ');
INSERT INTO ids SELECT 'gen', negocio.alta_cliente(pg_temp.uid('o'),pg_temp.uid('s'),NULL,'Obra Ficticia',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'');
SELECT pg_temp.afirmar((SELECT count(*)=2 FROM negocio.cliente),'alta visible por RLS en su sucursal');
SELECT pg_temp.afirmar((SELECT f.colonia='Centro' AND f.numero_exterior IS NULL AND f.registrada_por_acceso_id=pg_temp.uid('esc')
  FROM negocio.cliente c JOIN negocio.ficha_empresa f ON f.empresa_id=c.empresa_id WHERE c.id=(SELECT valor FROM ids WHERE clave='aut')),'ficha con vacíos como NULL y autor');
SELECT pg_temp.afirmar((SELECT a.numero='FI-999' AND a.autoridad='Medio ambiente' AND a.finalidad='Generador'
  FROM negocio.cliente c JOIN negocio.autorizacion_empresa a ON a.empresa_id=c.empresa_id WHERE c.id=(SELECT valor FROM ids WHERE clave='aut')),'autorización de medio ambiente recortada');
SELECT pg_temp.afirmar((SELECT count(*)=0 FROM negocio.cliente c JOIN negocio.autorizacion_empresa a ON a.empresa_id=c.empresa_id
  WHERE c.id=(SELECT valor FROM ids WHERE clave='gen')),'cliente general sin autorización');
SELECT pg_temp.rechaza($q$SELECT negocio.alta_cliente(pg_temp.uid('o'),pg_temp.uid('s'),' ','',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL)$q$,'22023','cliente sin nombre');
SELECT pg_temp.rechaza($q$SELECT negocio.alta_cliente(pg_temp.uid('o'),pg_temp.uid('s2'),'X',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL)$q$,'42501','sin escritura en otra sucursal');
-- Corrección: ficha nueva y autorización nueva; la anterior se conserva.
SELECT pg_temp.afirmar(negocio.corregir_cliente(pg_temp.uid('o'),pg_temp.uid('s'),(SELECT valor FROM ids WHERE clave='aut'),1,
  'Ferrocarril Ficticio S.A.',NULL,NULL,'Carretera Ficticia km 5','12','Centro','66050','El Carmen','Nuevo León','81 0000 0000',NULL,'FI-1000')=2,'corrección crea revisión 2');
SELECT pg_temp.afirmar((SELECT count(*)=2 FROM negocio.cliente c JOIN negocio.autorizacion_empresa a ON a.empresa_id=c.empresa_id
  WHERE c.id=(SELECT valor FROM ids WHERE clave='aut')),'la autorización anterior se conserva');
SELECT pg_temp.afirmar(negocio.corregir_cliente(pg_temp.uid('o'),pg_temp.uid('s'),(SELECT valor FROM ids WHERE clave='aut'),2,
  'Ferrocarril Ficticio S.A.',NULL,NULL,'Carretera Ficticia km 5','12','Centro','66050','El Carmen','Nuevo León','81 0000 0000',NULL,'FI-1000')=2,'sin cambios no crea revisión');
SELECT pg_temp.rechaza($q$SELECT negocio.corregir_cliente(pg_temp.uid('o'),pg_temp.uid('s'),(SELECT valor FROM ids WHERE clave='aut'),1,'X',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'FI-1000')$q$,'40001','revisión vieja');
SELECT pg_temp.rechaza($q$SELECT negocio.corregir_cliente(pg_temp.uid('o'),pg_temp.uid('s'),(SELECT valor FROM ids WHERE clave='aut'),2,'X',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL)$q$,'22023','no se quita la autorización');
SELECT pg_temp.rechaza($q$SELECT negocio.corregir_cliente(pg_temp.uid('o'),pg_temp.uid('s2'),(SELECT valor FROM ids WHERE clave='aut'),2,'X',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'FI-1000')$q$,'42501','corregir en otra sucursal');
SELECT pg_temp.rechaza($q$SELECT negocio.corregir_cliente(pg_temp.uid('o'),pg_temp.uid('s'),gen_random_uuid(),1,'X',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL)$q$,'P0002','cliente inexistente');
-- Un cliente general puede recibir autorización después.
SELECT pg_temp.afirmar(negocio.corregir_cliente(pg_temp.uid('o'),pg_temp.uid('s'),(SELECT valor FROM ids WHERE clave='gen'),1,
  NULL,'Obra Ficticia',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,'FI-2000')=1,'autorizar a un cliente general sin cambiar su ficha');
SELECT pg_temp.contexto('lee');
SELECT pg_temp.rechaza($q$SELECT negocio.alta_cliente(pg_temp.uid('o'),pg_temp.uid('s'),'X',NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL,NULL)$q$,'42501','solo lectura no da de alta');
SELECT pg_temp.afirmar((SELECT count(*)=2 FROM negocio.cliente),'solo lectura ve los clientes');
COMMIT;
SELECT count(*) AS comprobaciones_clientes FROM comprobacion_clientes;
\echo 'Comprobaciones de clientes terminadas sin error.'
