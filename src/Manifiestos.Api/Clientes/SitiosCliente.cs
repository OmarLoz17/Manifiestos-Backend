using Manifiestos.Api.Datos;
using Manifiestos.Api.Rutas;

namespace Manifiestos.Api.Clientes;

public static class SitiosCliente
{
    public static async Task<Dictionary<(Guid Sucursal, Guid Cliente), List<SitioCliente>>> LeerAsync(
        TransaccionContexto tx, Guid org, Guid cliente, CancellationToken ct)
    {
        var lista = new Dictionary<(Guid, Guid), List<SitioCliente>>();
        await using var cmd = tx.Comando("""
            SELECT cs.sucursal_id, cs.cliente_id, cs.sitio_id, cs.revision, coalesce(f.revision,0),
                   f.nombre, f.calle, f.numero_exterior, f.colonia, f.codigo_postal, f.municipio, f.estado,
                   cs.banos_registrados, cs.fosas_registradas,
                   EXISTS(SELECT 1 FROM negocio.parada p
                     JOIN LATERAL (SELECT pv.id,pv.sitio_id FROM negocio.parada_version pv
                       WHERE (pv.organizacion_id,pv.sucursal_id,pv.parada_id)=(p.organizacion_id,p.sucursal_id,p.id)
                       ORDER BY pv.revision DESC LIMIT 1) v ON v.sitio_id=cs.sitio_id
                     JOIN negocio.parada_cliente pc ON (pc.organizacion_id,pc.sucursal_id,pc.version_id)=(p.organizacion_id,p.sucursal_id,v.id)
                     WHERE p.organizacion_id=cs.organizacion_id AND p.sucursal_id=cs.sucursal_id AND pc.cliente_id=cs.cliente_id)
            FROM (SELECT DISTINCT ON (organizacion_id,sucursal_id,cliente_id,sitio_id) *
                  FROM negocio.cliente_sitio WHERE organizacion_id=$1
                    AND ($2::uuid='00000000-0000-0000-0000-000000000000'::uuid OR cliente_id=$2)
                  ORDER BY organizacion_id,sucursal_id,cliente_id,sitio_id,revision DESC) cs
            LEFT JOIN LATERAL (SELECT * FROM negocio.ficha_sitio fs
                WHERE (fs.organizacion_id,fs.sucursal_id,fs.sitio_id)=(cs.organizacion_id,cs.sucursal_id,cs.sitio_id)
                ORDER BY fs.revision DESC LIMIT 1) f ON true
            WHERE cs.activo
            ORDER BY f.nombre NULLS LAST,cs.sitio_id
            """, org, cliente);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            string? T(int i) => r.IsDBNull(i) ? null : r.GetString(i);
            var clave = (r.GetGuid(0),r.GetGuid(1));
            if (!lista.TryGetValue(clave,out var sitios)) lista[clave]=sitios=[];
            sitios.Add(new SitioCliente(r.GetGuid(2),r.GetInt32(3),r.GetInt32(4),T(5),T(6),T(7),T(8),T(9),T(10),T(11),
                r.IsDBNull(12)?null:r.GetInt32(12),r.IsDBNull(13)?null:r.GetInt32(13),r.GetBoolean(14)));
        }
        return lista;
    }

    public static async Task<ResultadoEscritura?> GuardarAsync(TransaccionContexto tx, Guid org, Guid suc, Guid cliente,
        SolicitudSitioCliente[]? sitios, CancellationToken ct)
    {
        if (sitios is null) return null; // Cliente antiguo: omitir no borra ni cambia direcciones.
        foreach (var s in sitios.Where(s=>s.SitioId.HasValue).OrderBy(s=>s.SitioId))
        {
            var id = s.SitioId!.Value;
            await using (var candado=tx.Comando("SELECT pg_advisory_xact_lock(hashtextextended('sitio:' || $1::text || ':' || $2::text,0))",org,id))
                await candado.ExecuteNonQueryAsync(ct);
        }
        var actuales=(await LeerAsync(tx,org,cliente,ct)).GetValueOrDefault((suc,cliente))??[];
        foreach (var s in sitios)
        {
            var actual=s.SitioId is {} buscado?actuales.SingleOrDefault(x=>x.SitioId==buscado):null;
            if (s.SitioId.HasValue && actual is null) return ResultadoEscritura.Invalida("La dirección no está registrada para este cliente.");
            if (actual is not null && (actual.Revision!=s.Revision || actual.RevisionFicha!=s.RevisionFicha))
                return ResultadoEscritura.Conflicto("Alguien más modificó una dirección. Recarga para ver los cambios.");
            if (s.Eliminar)
            {
                if (actual is null) return ResultadoEscritura.Invalida("La dirección todavía no está registrada.");
                if (actual.EnRuta) return ResultadoEscritura.Invalida("No puedes eliminar una dirección que se usa en una ruta para este cliente.");
                await using var baja=tx.Comando("""
                    INSERT INTO negocio.cliente_sitio (organizacion_id,sucursal_id,cliente_id,sitio_id,revision,activo,banos_registrados,fosas_registradas)
                    VALUES ($1,$2,$3,$4,$5,false,$6::int,$7::int)
                    """,org,suc,cliente,actual.SitioId,actual.Revision+1,N(actual.Banos),N(actual.Fosas));
                await baja.ExecuteNonQueryAsync(ct);
                continue;
            }
            var id=actual?.SitioId??Guid.NewGuid();
            if (actual is null)
            {
                await using var sitio=tx.Comando("INSERT INTO negocio.sitio (organizacion_id,sucursal_id,id,referencia) VALUES ($1,$2,$3,$4::text)",org,suc,id,N(s.Nombre));
                await sitio.ExecuteNonQueryAsync(ct);
                await using var punto=tx.Comando("INSERT INTO negocio.punto_servicio (organizacion_id,sucursal_id,sitio_id) VALUES ($1,$2,$3)",org,suc,id);
                await punto.ExecuteNonQueryAsync(ct);
            }
            var cambioDireccion=actual is null ||
                (actual.Nombre,actual.Calle,actual.NumeroExterior,actual.Colonia,actual.CodigoPostal,actual.Municipio,actual.Estado)!=
                (s.Nombre,s.Calle,s.NumeroExterior,s.Colonia,s.CodigoPostal,s.Municipio,s.Estado);
            if (cambioDireccion)
            {
                await using var ficha=tx.Comando("""
                    INSERT INTO negocio.ficha_sitio (organizacion_id,sucursal_id,sitio_id,revision,nombre,calle,numero_exterior,colonia,codigo_postal,municipio,estado,pais,numero_interior,referencia_localizacion)
                    SELECT $1,$2,$3,$4,$5::text,$6::text,$7::text,$8::text,$9::text,$10::text,$11::text,previa.pais,previa.numero_interior,previa.referencia_localizacion
                    FROM (SELECT 1) base LEFT JOIN LATERAL (
                        SELECT pais,numero_interior,referencia_localizacion FROM negocio.ficha_sitio
                        WHERE organizacion_id=$1 AND sucursal_id=$2 AND sitio_id=$3 ORDER BY revision DESC LIMIT 1
                    ) previa ON true
                    """,org,suc,id,(actual?.RevisionFicha??0)+1,N(s.Nombre),N(s.Calle),N(s.NumeroExterior),N(s.Colonia),N(s.CodigoPostal),N(s.Municipio),N(s.Estado));
                await ficha.ExecuteNonQueryAsync(ct);
            }
            if (actual is null || actual.Banos!=s.Banos || actual.Fosas!=s.Fosas)
            {
                await using var vinculo=tx.Comando("""
                    INSERT INTO negocio.cliente_sitio (organizacion_id,sucursal_id,cliente_id,sitio_id,revision,banos_registrados,fosas_registradas)
                    VALUES ($1,$2,$3,$4,$5,$6::int,$7::int)
                    """,org,suc,cliente,id,(actual?.Revision??0)+1,N(s.Banos),N(s.Fosas));
                await vinculo.ExecuteNonQueryAsync(ct);
            }
        }
        return null;
    }
    private static object N(object? valor)=>valor??DBNull.Value;
}
