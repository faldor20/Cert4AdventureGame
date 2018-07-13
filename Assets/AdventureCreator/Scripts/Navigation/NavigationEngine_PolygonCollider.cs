﻿/*
 *
 *	Adventure Creator
 *	by Chris Burton, 2013-2018
 *	
 *	"NavigationEngine_PolygonCollider.cs"
 * 
 *	This script uses a Polygon Collider 2D to
 *	allow pathfinding in a scene. Since v1.37,
 *	it uses the Dijkstra algorithm, as found on
 *	http://rosettacode.org/wiki/Dijkstra%27s_algorithm
 * 
 */

using UnityEngine;
using System.Collections;
using System.Collections.Generic;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace AC
{

	public class NavigationEngine_PolygonCollider : NavigationEngine
	{
		
		public static Collider2D[] results = new Collider2D[1];

		private int MAXNODES = 1000;
		private List<float[,]> allCachedGraphs = new List<float[,]>();
		private float searchRadius = 0.02f;

		private Vector2 dir_n = new Vector2 (0f, 1f);
		private Vector2 dir_s = new Vector2 (0f, -1f);
		private Vector2 dir_w = new Vector2 (-1f, 0f);
		private Vector2 dir_e = new Vector2 (1f, 0f);

		private Vector2 dir_ne = new Vector2 (0.71f, 0.71f);
		private Vector2 dir_se = new Vector2 (0.71f, -0.71f);
		private Vector2 dir_sw = new Vector2 (-0.71f, -0.71f);
		private Vector2 dir_nw = new Vector2 (-0.71f, 0.71f);

		private Vector2 dir_nne = new Vector2 (0.37f, 0.93f);
		private Vector2 dir_nee = new Vector2 (0.93f, 0.37f);
		private Vector2 dir_see = new Vector2 (0.93f, -0.37f);
		private Vector2 dir_sse = new Vector2 (0.37f, -0.93f);
		private Vector2 dir_ssw = new Vector2 (-0.37f, -0.93f);
		private Vector2 dir_sww = new Vector2 (-0.93f, -0.37f);
		private Vector2 dir_nww = new Vector2 (-0.93f, 0.37f);
		private Vector2 dir_nnw = new Vector2 (-0.37f, 0.93f);

		private List<Vector2[]> allVertexData = new List<Vector2[]>();


		public override void OnReset (NavigationMesh navMesh)
		{
			if (!Application.isPlaying) return;

			is2D = true;
			ResetHoles (navMesh);

			if (navMesh != null && navMesh.characterEvasion != CharacterEvasion.None && navMesh.GetComponent <PolygonCollider2D>() != null)
			{
				PolygonCollider2D[] polys = navMesh.GetComponents <PolygonCollider2D>();

				if (polys != null && polys.Length > 1)
				{
					ACDebug.LogWarning ("Character evasion cannot occur for multiple PolygonColliders - only the first on the active NavMesh will be affected.");
				}

				for (int i=0; i<polys.Length; i++)
				{
					if (!polys[i].isTrigger)
					{
						ACDebug.LogWarning ("The PolygonCollider2D on " + navMesh.gameObject.name + " is not a Trigger.");
					}
				}
			}

			if (navMesh == null && KickStarter.settingsManager != null && KickStarter.settingsManager.movementMethod == MovementMethod.PointAndClick)
			{
				ACDebug.LogWarning ("Could not initialise NavMesh - was one set as the Default in the Settings Manager?");
			}
		}


		public override void TurnOn (NavigationMesh navMesh)
		{
			if (navMesh == null || KickStarter.settingsManager == null) return;

			if (LayerMask.NameToLayer (KickStarter.settingsManager.navMeshLayer) == -1)
			{
				ACDebug.LogError ("Can't find layer " + KickStarter.settingsManager.navMeshLayer + " - please define it in Unity's Tags Manager (Edit -> Project settings -> Tags and Layers).");
			}
			else if (KickStarter.settingsManager.navMeshLayer != "")
			{
				navMesh.gameObject.layer = LayerMask.NameToLayer (KickStarter.settingsManager.navMeshLayer);
			}
			
			if (navMesh.GetComponent <Collider2D>() == null)
			{
				ACDebug.LogWarning ("A 2D Collider component must be attached to " + navMesh.gameObject.name + " for pathfinding to work - please attach one.");
			}
		}


		public override Vector3[] GetPointsArray (Vector3 _originPos, Vector3 _targetPos, AC.Char _char = null)
		{
			if (KickStarter.sceneSettings == null || KickStarter.sceneSettings.navMesh == null)
			{
				return base.GetPointsArray (_originPos, _targetPos, _char);
			}

			PolygonCollider2D[] polys = KickStarter.sceneSettings.navMesh.transform.GetComponents <PolygonCollider2D>();
			if (polys == null || polys.Length == 0)
			{
				return base.GetPointsArray (_originPos, _targetPos, _char);
			}

			CalcSearchRadius (KickStarter.sceneSettings.navMesh);

			AddCharHoles (polys, _char, KickStarter.sceneSettings.navMesh);

			List<Vector3> pointsList3D = new List<Vector3> ();
			if (IsLineClear (_originPos, _targetPos))
			{
				pointsList3D.Add (_targetPos);
				return pointsList3D.ToArray ();
			}
			
			// Iterate through polys, find originPos neareset to _originPos
			int nearestOriginIndex = -1;
			float minDist = 0f;
			Vector2 originPos = Vector2.zero;
			Vector2 originPos2D = new Vector2 (_originPos.x, _originPos.y);
			for (int i=0; i<polys.Length; i++)
			{
				Vector2 testOriginPos = GetNearestToMesh (_originPos, polys[i], (polys.Length > 1));
				float dist = (originPos2D - testOriginPos).sqrMagnitude;
				if (nearestOriginIndex < 0 || dist < minDist)
				{
					minDist = dist;
					originPos = testOriginPos;
					nearestOriginIndex = i;
				}
			}
			if (nearestOriginIndex < 0) nearestOriginIndex = 0;
			Vector2 targetPos = GetNearestToMesh (_targetPos, polys[nearestOriginIndex], (polys.Length > 1));

			Vector2[] pointsList = allVertexData[nearestOriginIndex];
			pointsList = AddEndsToList (pointsList, originPos, targetPos);

			bool useCache = (KickStarter.sceneSettings.navMesh.characterEvasion == CharacterEvasion.None) ? true : false;

			float[,] weight = pointsToWeight (pointsList, useCache, nearestOriginIndex);
			int[] precede = buildSpanningTree (0, 1, weight);
			if (precede == null)
			{
				ACDebug.LogWarning ("Pathfinding error - cannot build spanning tree from " + originPos + " to " + targetPos);
				pointsList3D.Add (_targetPos);
				return pointsList3D.ToArray ();
			}
			
			int[] _path = getShortestPath (0, 1, precede);
			foreach (int i in _path)
			{
				if (i < pointsList.Length)
				{
					Vector3 vertex = new Vector3 (pointsList[i].x, pointsList[i].y, _originPos.z);
					pointsList3D.Insert (0, vertex);
				}
			}
			
			if (pointsList3D.Count > 1)
			{
				if (pointsList3D[0] == _originPos || (pointsList3D[0].x == originPos.x && pointsList3D[0].y == originPos.y))
				{
					pointsList3D.RemoveAt (0);	// Remove origin point from start
				}
			}
			else if (pointsList3D.Count == 0)
			{
				ACDebug.LogError ("Error attempting to pathfind to point " + _targetPos + " corrected = " + targetPos);
				pointsList3D.Add (originPos);
			}

			return pointsList3D.ToArray ();
		}


		public override void ResetHoles (NavigationMesh navMesh)
		{
			ResetHoles (navMesh, true);
		}


		private void ResetHoles (NavigationMesh navMesh, bool rebuild)
		{
			if (navMesh == null) return;
			CalcSearchRadius (navMesh);

			PolygonCollider2D[] polys = navMesh.GetComponents <PolygonCollider2D>();
			if (polys == null || polys.Length == 0) return;

			for (int p=0; p<polys.Length; p++)
			{
				polys[p].pathCount = 1;

				// Holes can only go in the first polygon
				if (p > 0 || navMesh.polygonColliderHoles.Count == 0)
				{
					if (rebuild)
					{
						RebuildVertexArray (navMesh.transform, polys[p], p);
						CreateCache (p);
					}
					continue;
				}

				Vector2 scaleFac = new Vector2 (1f / navMesh.transform.localScale.x, 1f / navMesh.transform.localScale.y);
				foreach (PolygonCollider2D hole in navMesh.polygonColliderHoles)
				{
					if (hole != null)
					{
						polys[p].pathCount ++;
						
						List<Vector2> newPoints = new List<Vector2>();
						foreach (Vector2 holePoint in hole.points)
						{
							Vector2 relativePosition = hole.transform.TransformPoint (holePoint) - navMesh.transform.position;
							newPoints.Add (new Vector2 (relativePosition.x * scaleFac.x, relativePosition.y * scaleFac.y));
						}
						
						polys[p].SetPath (polys[p].pathCount-1, newPoints.ToArray ());
						hole.gameObject.layer = LayerMask.NameToLayer (KickStarter.settingsManager.deactivatedLayer);
						hole.isTrigger = true;
					}
				}

				if (rebuild)
				{
					RebuildVertexArray (navMesh.transform, polys[p], p);
					CreateCache (p);
				}
			}
		}


		public override Vector3 GetPointNear (Vector3 point, float minDistance, float maxDistance)
		{
			Vector2 randomOffset = Random.insideUnitCircle * Random.Range (minDistance, maxDistance);
			Vector2 randomPoint = (Vector2) point + randomOffset;

			if (IsLineClear (point, randomPoint))
			{
				return randomPoint;
			}

			Vector2 intersectPoint = GetLineIntersect (randomPoint, point);
			if (intersectPoint != Vector2.zero)
			{
				return intersectPoint;
			}

			return base.GetPointNear (point, minDistance, maxDistance);
		}
		
		
		private int[] buildSpanningTree (int source, int destination, float[,] weight)
		{
			int n = (int) Mathf.Sqrt (weight.Length);
			
			bool[] visit = new bool[n];
			float[] distance = new float[n];
			int[] precede = new int[n];
			
			for (int i=0 ; i<n ; i++)
			{
				distance[i] = Mathf.Infinity;
				precede[i] = 100000;
			}
			distance[source] = 0;
			
			int current = source;
			while (current != destination)
			{
				if (current < 0)
				{
					return null;
				}
				
				float distcurr = distance[current];
				float smalldist = Mathf.Infinity;
				int k = -1;
				visit[current] = true;
				
				for (int i=0; i<n; i++)
				{
					if (visit[i])
					{
						continue;
					}
					
					float newdist = distcurr + weight[current,i];
					if (weight[current,i] == -1f)
					{
						newdist = Mathf.Infinity;
					}
					if (newdist < distance[i])
					{
						distance[i] = newdist;
						precede[i] = current;
					}
					if (distance[i] < smalldist)
					{
						smalldist = distance[i];
						k = i;
					}
				}
				current = k;
			}
			
			return precede;
		}
		
		
		private int[] getShortestPath (int source, int destination, int[] precede)
		{
			int i = destination;
			int finall = 0;
			int[] path = new int[MAXNODES];
			
			path[finall] = destination;
			finall++;
			while (precede[i] != source)
			{
				i = precede[i];
				path[finall] = i;
				finall ++;
			}
			path[finall] = source;
			
			int[] result = new int[finall+1];
			
			for (int j=0; j<finall+1; j++)
			{
				result[j] = path[j];
			}
			
			return result;
		}
		

		private float[,] pointsToWeight (Vector2[] points, bool useCache = false, int polyIndex = 0)
		{
			int n = points.Length;
			int m = n;
			float[,] graph = new float [n, n];
			if (useCache)
			{
				graph = allCachedGraphs [polyIndex];
				n = 2;
			}

			for (int i=0; i<n; i++)
			{
				for (int j=i; j<m; j++)
				{
					if (i==j)
					{
						graph[i,j] = -1f;
					}
					else if (!IsLineClear (points[i], points[j]))
					{
						graph[i,j] = graph[j,i] = -1f;
					}
					else
					{
						graph[i,j] = graph[j,i] = (points[i] - points[j]).magnitude; // Sqr?
					}
				}
			}
			return graph;
		}


		private Vector2 GetNearestToMesh (Vector2 vertex, PolygonCollider2D poly, bool hasMultiple)
		{
			// Test to make sure starting on the collision mesh
			RaycastHit2D hit = UnityVersionHandler.Perform2DRaycast
			(
				vertex - new Vector2 (0.005f, 0f),
				new Vector2 (1f, 0f),
				0.01f,
				1 << KickStarter.sceneSettings.navMesh.gameObject.layer
			);

			if (!hit)
			{
				// Horizontal didn't work, try vertical
				hit = UnityVersionHandler.Perform2DRaycast
				(
					vertex - new Vector2 (0f, 0.005f),
					new Vector2 (0f, 1f),
					0.01f,
					1 << KickStarter.sceneSettings.navMesh.gameObject.layer
				);
			}

			if (!hit)
			{
				return GetNearestOffMesh (vertex, poly);
			}
			else if (hasMultiple)
			{
				if (hit.collider != null && hit.collider is PolygonCollider2D && hit.collider != poly)
				{
					return GetNearestOffMesh (vertex, poly);
				}
			}
			return (vertex);	
		}


		private Vector2 GetNearestOffMesh (Vector2 vertex, PolygonCollider2D poly)
		{
			Transform t = KickStarter.sceneSettings.navMesh.transform;
			float minDistance = -1;
			Vector2 nearestPoint = vertex;
			Vector2 testPoint = vertex;

			for (int i=0; i<poly.pathCount; i++)
			{
				Vector2[] path = poly.GetPath (i);

				for (int j=0; j<path.Length; j++)
				{
					Vector2 startPoint = t.TransformPoint (path[j]);
					Vector2 endPoint = Vector2.zero;
					if (j==path.Length-1)
					{
						endPoint = t.TransformPoint (path[0]);
					}
					else
					{
						endPoint = t.TransformPoint (path[j+1]);
					}

					Vector2 direction = endPoint - startPoint;
					for (float k=0f; k<=1f; k+=0.1f)
					{
						testPoint = startPoint + (direction * k);
						float distance = (vertex - testPoint).sqrMagnitude; // Was .magnitude

						if (distance < minDistance || minDistance < 0f)
						{
							minDistance = distance;
							nearestPoint = testPoint;
						}
					}
				}
			}
			return nearestPoint;
		}

		
		private Vector2[] AddEndsToList (Vector2[] points, Vector2 originPos, Vector2 targetPos, bool checkForDuplicates = true)
		{
			List<Vector2> newPoints = new List<Vector2>();

			foreach (Vector2 point in points)
			{
				if ((point != originPos && point != targetPos) || !checkForDuplicates)
				{
					newPoints.Add (point);
				}
			}

			newPoints.Insert (0, targetPos);
			newPoints.Insert (0, originPos);
			
			return newPoints.ToArray ();
		}


		private bool IsLineClear (Vector2 startPos, Vector2 endPos)
		{
			// This will test if points can "see" each other, by doing a circle overlap check along the line between them
			Vector2 actualPos = startPos;
			Vector2 direction = (endPos - startPos).normalized;
			float magnitude = (endPos - startPos).magnitude;

			float searchStep = 100f * searchRadius * searchRadius; // squared so that gap between circles increases with radius

			for (float i=0f; i<magnitude; i+= searchStep)
			{
				actualPos = startPos + (direction * i);

				if (UnityVersionHandler.Perform2DOverlapCircle (actualPos, searchRadius, NavigationEngine_PolygonCollider.results, 1 << KickStarter.sceneSettings.navMesh.gameObject.layer) != 1)
				{
					return false;
				}
			}

			return true;
		}


		private Vector2 GetLineIntersect (Vector2 startPos, Vector2 endPos)
		{
			// Important: startPos is considered to be outside the NavMesh

			Vector2 actualPos = startPos;
			Vector2 direction = (endPos - startPos).normalized;
			float magnitude = (endPos - startPos).magnitude;

			int numInside = 0;
			
			float radius = magnitude * 0.02f;
			for (float i=0f; i<magnitude; i+= (radius * 2f))
			{
				actualPos = startPos + (direction * i);

				if (UnityVersionHandler.Perform2DOverlapCircle (actualPos, radius, NavigationEngine_PolygonCollider.results, 1 << KickStarter.sceneSettings.navMesh.gameObject.layer) != 0)
				{
					numInside ++;
				}
				if (numInside == 2)
				{
					return actualPos;
				}
			}
			return Vector2.zero;
		}
		

		public override string GetPrefabName ()
		{
			return ("NavMesh2D");
		}
		
		
		public override void SetVisibility (bool visibility)
		{
			#if UNITY_EDITOR
			NavigationMesh[] navMeshes = FindObjectsOfType (typeof (NavigationMesh)) as NavigationMesh[];
			Undo.RecordObjects (navMeshes, "Navigation visibility");
			
			foreach (NavigationMesh navMesh in navMeshes)
			{
				navMesh.showInEditor = visibility;
				UnityVersionHandler.CustomSetDirty (navMesh, true);
			}
			#endif
		}
		
		
		public override void SceneSettingsGUI ()
		{
			#if UNITY_EDITOR
			EditorGUILayout.BeginHorizontal ();
			KickStarter.sceneSettings.navMesh = (NavigationMesh) EditorGUILayout.ObjectField ("Default NavMesh:", KickStarter.sceneSettings.navMesh, typeof (NavigationMesh), true);
			if (!SceneSettings.IsUnity2D ())
			{
				EditorGUILayout.HelpBox ("This method is only compatible with 'Unity 2D' mode.", MessageType.Warning);
			}
			else if (KickStarter.sceneSettings.navMesh == null)
			{
				if (GUILayout.Button ("Create", GUILayout.MaxWidth (60f)))
				{
					NavigationMesh newNavMesh = null;
					newNavMesh = SceneManager.AddPrefab ("Navigation", "NavMesh2D", true, false, true).GetComponent <NavigationMesh>();

					newNavMesh.gameObject.name = "Default NavMesh";
					KickStarter.sceneSettings.navMesh = newNavMesh;
					EditorGUIUtility.PingObject (newNavMesh.gameObject);
				}
			}
			EditorGUILayout.EndHorizontal ();
			#endif
		}


		private void AddCharHoles (PolygonCollider2D[] navPolys, AC.Char charToExclude, NavigationMesh navigationMesh)
		{
			if (navigationMesh.characterEvasion == CharacterEvasion.None)
			{
				return;
			}

			ResetHoles (KickStarter.sceneSettings.navMesh, false);

			for (int p=0; p<navPolys.Length; p++)
			{
				if (p > 0)
				{
					return;
				}

				if (navPolys[p].transform.lossyScale != Vector3.one)
				{
					ACDebug.LogWarning ("Cannot create evasion Polygons inside NavMesh '" + navPolys[p].gameObject.name + "' because it has a non-unit scale.");
					continue;
				}

				Vector2 navPosition = navPolys[p].transform.position;

				foreach (AC.Char character in KickStarter.stateHandler.Characters)
				{
					CircleCollider2D circleCollider2D = character.GetComponent <CircleCollider2D>();
					if (circleCollider2D != null &&
						(character.charState == CharState.Idle || navigationMesh.characterEvasion == CharacterEvasion.AllCharacters) &&
					    (charToExclude == null || character != charToExclude) && 
						UnityVersionHandler.Perform2DOverlapPoint (character.transform.position, NavigationEngine_PolygonCollider.results, 1 << KickStarter.sceneSettings.navMesh.gameObject.layer) != 0)
					{
						circleCollider2D.isTrigger = true;
						List<Vector2> newPoints3D = new List<Vector2>();
						
						#if UNITY_5 || UNITY_2017_1_OR_NEWER
						Vector2 centrePoint = character.transform.TransformPoint (circleCollider2D.offset);
						#else
						Vector2 centrePoint = character.transform.TransformPoint (circleCollider2D.center);
						#endif
						
						float radius = circleCollider2D.radius * character.transform.localScale.x;
						float yScaler = navigationMesh.characterEvasionYScale;

						if (navigationMesh.characterEvasionPoints == CharacterEvasionPoints.Four)
						{
							newPoints3D.Add (centrePoint + new Vector2 (dir_n.x * radius, dir_n.y * radius * yScaler));
							newPoints3D.Add (centrePoint + new Vector2 (dir_e.x * radius, dir_e.y * radius * yScaler));
							newPoints3D.Add (centrePoint + new Vector2 (dir_s.x * radius, dir_s.y * radius * yScaler));
							newPoints3D.Add (centrePoint + new Vector2 (dir_w.x * radius, dir_w.y * radius * yScaler));
						}
						else if (navigationMesh.characterEvasionPoints == CharacterEvasionPoints.Eight)
						{
							newPoints3D.Add (centrePoint + new Vector2 (dir_n.x * radius, dir_n.y * radius * yScaler));
							newPoints3D.Add (centrePoint + new Vector2 (dir_ne.x * radius, dir_ne.y * radius * yScaler));

							newPoints3D.Add (centrePoint + new Vector2 (dir_e.x * radius, dir_e.y * radius * yScaler));
							newPoints3D.Add (centrePoint + new Vector2 (dir_se.x * radius, dir_se.y * radius * yScaler));

							newPoints3D.Add (centrePoint + new Vector2 (dir_s.x * radius, dir_s.y * radius * yScaler));
							newPoints3D.Add (centrePoint + new Vector2 (dir_sw.x * radius, dir_sw.y * radius * yScaler));

							newPoints3D.Add (centrePoint + new Vector2 (dir_w.x * radius, dir_w.y * radius * yScaler));
							newPoints3D.Add (centrePoint + new Vector2 (dir_nw.x * radius, dir_nw.y * radius * yScaler));
						}
						else if (navigationMesh.characterEvasionPoints == CharacterEvasionPoints.Sixteen)
						{
							newPoints3D.Add (centrePoint + new Vector2 (dir_n.x * radius, dir_n.y * radius * yScaler));
							newPoints3D.Add (centrePoint + new Vector2 (dir_nne.x * radius, dir_nne.y * radius * yScaler));
							newPoints3D.Add (centrePoint + new Vector2 (dir_ne.x * radius, dir_ne.y * radius * yScaler));
							newPoints3D.Add (centrePoint + new Vector2 (dir_nee.x * radius, dir_nee.y * radius * yScaler));

							newPoints3D.Add (centrePoint + new Vector2 (dir_e.x * radius, dir_e.y * radius * yScaler));
							newPoints3D.Add (centrePoint + new Vector2 (dir_see.x * radius, dir_see.y * radius * yScaler));
							newPoints3D.Add (centrePoint + new Vector2 (dir_se.x * radius, dir_se.y * radius * yScaler));
							newPoints3D.Add (centrePoint + new Vector2 (dir_sse.x * radius, dir_sse.y * radius * yScaler));

							newPoints3D.Add (centrePoint + new Vector2 (dir_s.x * radius, dir_s.y * radius * yScaler));
							newPoints3D.Add (centrePoint + new Vector2 (dir_ssw.x * radius, dir_ssw.y * radius * yScaler));
							newPoints3D.Add (centrePoint + new Vector2 (dir_sw.x * radius, dir_sw.y * radius * yScaler));
							newPoints3D.Add (centrePoint + new Vector2 (dir_sww.x * radius, dir_sww.y * radius * yScaler));

							newPoints3D.Add (centrePoint + new Vector2 (dir_w.x * radius, dir_w.y * radius * yScaler));
							newPoints3D.Add (centrePoint + new Vector2 (dir_nww.x * radius, dir_nww.y * radius * yScaler));
							newPoints3D.Add (centrePoint + new Vector2 (dir_nw.x * radius, dir_nw.y * radius * yScaler));
							newPoints3D.Add (centrePoint + new Vector2 (dir_nnw.x * radius, dir_nnw.y * radius * yScaler));
						}

						navPolys[p].pathCount ++;
						
						List<Vector2> newPoints = new List<Vector2>();
						for (int i=0; i<newPoints3D.Count; i++)
						{
							// Only add a point if it is on the NavMesh
							if (UnityVersionHandler.Perform2DOverlapPoint (newPoints3D[i], NavigationEngine_PolygonCollider.results, 1 << KickStarter.sceneSettings.navMesh.gameObject.layer) != 0)
							{
								newPoints.Add (newPoints3D[i] - navPosition);
							}
							else
							{
								Vector2 altPoint = GetLineIntersect (newPoints3D[i], centrePoint);
								if (altPoint != Vector2.zero)
								{
									newPoints.Add (altPoint - navPosition);
								}
							}
						}

						if (newPoints.Count > 1)
						{
							navPolys[p].SetPath (navPolys[p].pathCount-1, newPoints.ToArray ());
						}
					}
				}

				RebuildVertexArray (navPolys[p].transform, navPolys[p], p);
			}
		}


		private void RebuildVertexArray (Transform navMeshTransform, PolygonCollider2D poly, int polyIndex)
		{
			if (allVertexData == null)
			{
				allVertexData = new List<Vector2[]>();
			}
			if (allVertexData.Count <= polyIndex)
			{
				while (allVertexData.Count <= polyIndex)
				{
					allVertexData.Add (new Vector2[0]);
				}
			}

			List<Vector2> _vertexData = new List<Vector2>();
			for (int i=0; i<poly.pathCount; i++)
			{
				Vector2[] _vertices = poly.GetPath (i);
				for (int v=0; v<_vertices.Length; v++)
				{
					Vector3 vertex3D = navMeshTransform.TransformPoint (new Vector3 (_vertices[v].x, _vertices[v].y, navMeshTransform.position.z));
					_vertexData.Add (new Vector2 (vertex3D.x, vertex3D.y));
				}
			}

			allVertexData [polyIndex] = _vertexData.ToArray ();
		}


		private void CalcSearchRadius (NavigationMesh navMesh)
		{
			searchRadius = 0.1f - (0.08f * navMesh.accuracy);
		}
		

		private void CreateCache (int i)
		{
			if (!Application.isPlaying)
			{
				return;
			}
		
			// Create table of weights with "dummy" start/end vertices, as these are at the front anyway - so anything below will be the same anyway
			Vector2[] pointsList = allVertexData[i];
			
			Vector2 originPos = Vector2.zero;
			Vector2 targetPos = Vector2.zero;

			pointsList = AddEndsToList (pointsList, originPos, targetPos, false);

			if (allCachedGraphs == null)
			{
				allCachedGraphs = new List<float[,]>();
			}
			if (allCachedGraphs.Count <= i)
			{
				while (allCachedGraphs.Count <= i)
				{
					allCachedGraphs.Add (new float[0,0]);
				}
			}

			allCachedGraphs[i] = pointsToWeight (pointsList, false, i);

			#if UNITY_ANDROID || UNITY_IOS
			if (KickStarter.sceneSettings.navMesh != null && KickStarter.sceneSettings.navMesh.characterEvasion != CharacterEvasion.None)
			{
				ACDebug.Log ("The NavMesh's 'Character evasion' setting should be set to 'None' for best performance on mobile devices.");
			}
			#endif
		}


		#if UNITY_EDITOR

		public override NavigationMesh NavigationMeshGUI (NavigationMesh _target)
		{
			_target = base.NavigationMeshGUI (_target);

			_target.characterEvasion = (CharacterEvasion) EditorGUILayout.EnumPopup ("Character evasion:", _target.characterEvasion);
			if (_target.characterEvasion != CharacterEvasion.None)
			{
				_target.characterEvasionPoints = (CharacterEvasionPoints) EditorGUILayout.EnumPopup ("Evasion accuracy:", _target.characterEvasionPoints);
				_target.characterEvasionYScale = EditorGUILayout.Slider ("Evasion y-scale:", _target.characterEvasionYScale, 0.1f, 1f);

				EditorGUILayout.HelpBox ("Note: Characters can only be avoided if they have a Circle Collider 2D (no Trigger) component on their base.\n\n" +
					"For best results, set a non-zero 'Pathfinding update time' in the Settings Manager.", MessageType.Info);

				if (_target.transform.lossyScale != Vector3.one)
				{
					EditorGUILayout.HelpBox ("For character evasion to work, the NavMesh must have a unit scale (1,1,1).", MessageType.Warning);
				}

				#if UNITY_ANDROID || UNITY_IOS
				EditorGUILayout.HelpBox ("This is an expensive calculation - consider setting this to 'None' for mobile platforms.", MessageType.Warning);
				#endif
			}

			_target.accuracy = EditorGUILayout.Slider ("Accuracy:", _target.accuracy, 0f, 1f);
			_target.gizmoColour = EditorGUILayout.ColorField ("Gizmo colour:", _target.gizmoColour);


			EditorGUILayout.Separator ();
			GUILayout.Box("", GUILayout.ExpandWidth(true), GUILayout.Height(1));
			EditorGUILayout.LabelField ("NavMesh holes", EditorStyles.boldLabel);

			for (int i=0; i<_target.polygonColliderHoles.Count; i++)
			{
				EditorGUILayout.BeginHorizontal ();
				_target.polygonColliderHoles [i] = (PolygonCollider2D) EditorGUILayout.ObjectField ("Hole #" + i.ToString () + ":", _target.polygonColliderHoles [i], typeof (PolygonCollider2D), true);

				if (GUILayout.Button ("-", GUILayout.MaxWidth (20f)))
				{
					_target.polygonColliderHoles.RemoveAt (i);
					i=-1;
				}

				EditorGUILayout.EndHorizontal ();

				if (_target.polygonColliderHoles[i] != null && _target.polygonColliderHoles[i].GetComponent <NavMeshBase>())
				{
					EditorGUILayout.HelpBox ("A NavMesh cannot use its own Polygon Collider component as a hole!", MessageType.Warning);
				}
			}

			if (GUILayout.Button ("Create new hole"))
			{
				_target.polygonColliderHoles.Add (null);
			}

			if (_target.GetComponent <PolygonCollider2D>() != null)
			{
				int numPolys = _target.GetComponents <PolygonCollider2D>().Length;
				if (numPolys > 1)
				{
					if (_target.polygonColliderHoles.Count > 0)
					{
						EditorGUILayout.HelpBox ("Holes will only work if they are within the boundaries of " + _target.gameObject.name + "'s FIRST PolygonCollider component.", MessageType.Warning);
					}
					if (_target.characterEvasion != CharacterEvasion.None)
					{
						EditorGUILayout.HelpBox ("Character-evasion will only work within the boundaries of " + _target.gameObject.name + "'s FIRST PolygonCollider component.", MessageType.Warning);
					}
				}
			}

			return _target;
		}


		public override void DrawGizmos (GameObject navMeshOb)
		{
			if (navMeshOb != null)
			{
				Color gizmoColour = Color.white;

				if (navMeshOb.GetComponent <NavigationMesh>())
				{
					gizmoColour = navMeshOb.GetComponent <NavigationMesh>().gizmoColour;
				}

				PolygonCollider2D[] polys = navMeshOb.GetComponents <PolygonCollider2D>();
				if (polys != null)
				{
					for (int i=0; i<polys.Length; i++)
					{
						AdvGame.DrawPolygonCollider (navMeshOb.transform, polys[i], gizmoColour);
					}
				}
			}
		}

		#endif

	}
	
}